using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// RC9 live-preview measurement harness. All documents are generated in memory
// from fixed seeds. No user file is read.
internal static class LivePreviewBench {
    static readonly string Emoji = "\U0001F600";
    // The mermaid scenario waits this long for the diagrams in view before it starts measuring.
    const int MermaidWaitMilliseconds = 20000;
    // The mermaid scenario's warm-up pass, one scroll over the whole document before the measured
    // scroll: one viewport step waits at most this long for the diagrams in view, and the whole pass
    // stops after the total. Whatever is still not drawn is recorded in the result.
    const int MermaidWarmStepMilliseconds = 5000;
    // Renders run one at a time on the engine thread, and all 24 blocks may each need up to a step
    // bound, so the pass gets 150 s; that plus the settling wait, typing and the scroll still fits the
    // document's 300 s watchdog, and the pass stops early as soon as every block is drawn.
    const int MermaidWarmTotalMilliseconds = 150000;
    static Process process;
    static readonly StringBuilder diagnostics=new StringBuilder();
    static void BeginDiagnostics() {
        MarkdownLivePreview.DiagParses=MarkdownLivePreview.DiagParseTicks=MarkdownLivePreview.DiagDraws=0;
        MarkdownLivePreview.DiagMeasureCells=MarkdownLivePreview.DiagMeasureTicks=MarkdownLivePreview.DiagRows=MarkdownLivePreview.DiagRowTicks=0;
        MarkdownLivePreview.DiagTableTicks=MarkdownLivePreview.DiagMarkers=MarkdownLivePreview.DiagMarkerTicks=0;
    }
    static void EndDiagnostics(string scenario,string doc,string stage) {
        double ms=1000.0/Stopwatch.Frequency;
        diagnostics.Append(scenario).Append('\t').Append(doc).Append('\t').Append(stage).Append('\t')
            .Append(MarkdownLivePreview.DiagParses).Append('\t').Append((MarkdownLivePreview.DiagParseTicks*ms).ToString("F3",CultureInfo.InvariantCulture)).Append('\t')
            .Append(MarkdownLivePreview.DiagMeasureCells).Append('\t').Append((MarkdownLivePreview.DiagMeasureTicks*ms).ToString("F3",CultureInfo.InvariantCulture)).Append('\t')
            .Append(MarkdownLivePreview.DiagRows).Append('\t').Append((MarkdownLivePreview.DiagRowTicks*ms).ToString("F3",CultureInfo.InvariantCulture)).Append('\t')
            .Append((MarkdownLivePreview.DiagTableTicks*ms).ToString("F3",CultureInfo.InvariantCulture)).Append('\t')
            .Append(MarkdownLivePreview.DiagMarkers).Append('\t').Append((MarkdownLivePreview.DiagMarkerTicks*ms).ToString("F3",CultureInfo.InvariantCulture)).Append('\n');
    }

    internal static int Run(string outputDirectory){
        string outDir = null;
        bool quick = false;
        int exitCode = 0;
        string fatal = null;
        Application createdApplication = null;
        bool ownsApplication = false;
        List<ScenarioGroup> groups = new List<ScenarioGroup>();
        List<DocData> docs = null;
        EnvironmentInfo environment = null;
        MemorySnapshot finalDiagnostic = null;
        DateTime timestamp = DateTime.Now;
        Stopwatch total = Stopwatch.StartNew();

        try{
            if(String.IsNullOrEmpty(outputDirectory)) throw new ArgumentException("output directory is required");
            outDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outDir);
            quick = IsQuickDirectory(outDir);

            if(Application.Current == null){
                createdApplication = new Application();
                createdApplication.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ownsApplication = true;
            }
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            process = Process.GetCurrentProcess();
            environment = CaptureEnvironment();

            diagnostics.Length=0;
            diagnostics.Append("scenario\tdoc\tstage\tparses\tparse_ms\tmeasured_cells\tmeasure_ms\trows\trow_ms\ttable_work_ms\tmarkers\tmarker_ms\n");
            docs = BuildData(quick, outDir);
            groups.Add(RunEditorScenario("plain", false, SelectDocs(docs, "TXT_12M", "MD_1_9M", "MD_12M"), outDir));
            groups.Add(RunEditorScenario("live", true, SelectDocs(docs, "MD_1_9M", "MD_5M", "MD_12M", "MD_1_9M_CRLF", "STRESS_PARA", "STRESS_TABLE", "STRESS_CODE", "STRESS_MEDIA"), outDir));
            // A ```mermaid block only becomes a picture with the live preview attached, so the mermaid
            // document gets its own group with a fresh editor and window (like "plain" and "live"). The
            // group is selected like the others: every run measures every group; the folder name "-quick"
            // scales the generated sizes. MDNOTE_HIDE_MARKERS=1 makes this group the basic view.
            groups.Add(RunEditorScenario("mermaid", true, SelectDocs(docs, "MERMAID"), outDir));

            for(int i = 0; i < docs.Count; i++) docs[i].Text = null;
        }catch(Exception ex){
            fatal = Describe(ex);
            exitCode = 1;
        }finally{
            total.Stop();
            try{
                if(ownsApplication && createdApplication != null){
                    try{ createdApplication.Shutdown(); }catch{ }
                }
                finalDiagnostic = CaptureMemory(true);
            }catch(Exception ex){
                if(fatal == null) fatal = Describe(ex);
                exitCode = 1;
            }
            try{
                if(outDir != null){
                    if(fatal == null && groups.Count == 0){ fatal = "no scenarios were produced"; exitCode = 1; }
                    WriteOutputs(outDir, docs, groups, environment, timestamp, quick, total.Elapsed.TotalMilliseconds, fatal, finalDiagnostic);
                }
            }catch(Exception ex){
                string message = Describe(ex);
                fatal = fatal == null ? message : fatal + " | " + message;
                exitCode = 1;
            }
            try{ if(process != null) process.Dispose(); }catch{ }
            process = null;
        }

        if(fatal == null) Console.WriteLine("LivePreviewBench completed: " + outDir);
        else Console.WriteLine("LivePreviewBench failed: " + fatal);
        return exitCode;
    }

    static bool IsQuickDirectory(string directory){
        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if(String.IsNullOrEmpty(name)) name = directory;
        return name.EndsWith("-quick", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- data

    static List<DocData> BuildData(bool quick, string outDir){
        List<DocData> docs = new List<DocData>();
        int md19Target = ScaleSize(1900000, quick);
        int md5Target = ScaleSize(5000000, quick);
        int md12Target = ScaleSize(12000000, quick);
        int txtTarget = ScaleSize(12000000, quick);
        int paragraphTarget = ScaleSize(200000, quick);
        int tableRows = ScaleCount(5000, quick);
        int codeLines = ScaleCount(100000, quick);
        int mediaCount = quick ? 8 : 30;
        int mediaSections = quick ? 10 : 36;
        int mermaidBlocks = quick ? 6 : 24;
        int mermaidLines = quick ? 400 : 1500;

        string md19 = GenerateMarkdown(md19Target, 1001);
        docs.Add(MakeDoc("MD_1_9M", "markdown", 1900000, md19Target, md19, "LF",
            "mixed Markdown: headings H1-H4, lists, quotes, code, tables, emoji", quick));
        string md5 = GenerateMarkdown(md5Target, 1002);
        docs.Add(MakeDoc("MD_5M", "markdown", 5000000, md5Target, md5, "LF",
            "mixed Markdown: headings H1-H4, lists, quotes, code, tables, emoji", quick));
        string md12 = GenerateMarkdown(md12Target, 1003);
        docs.Add(MakeDoc("MD_12M", "markdown", 12000000, md12Target, md12, "LF",
            "mixed Markdown: headings H1-H4, lists, quotes, code, tables, emoji", quick));
        string crlf = md19.Replace("\n", "\r\n");
        docs.Add(MakeDoc("MD_1_9M_CRLF", "markdown", 1900000, md19Target, crlf, "CRLF",
            "same generated Markdown body converted to CRLF", quick));

        string txt = GeneratePlain(txtTarget, 2001);
        docs.Add(MakeDoc("TXT_12M", "txt", 12000000, txtTarget, txt, "LF",
            "plain Korean/English prose lines without Markdown syntax", quick));

        string paragraph = GenerateStressParagraph(paragraphTarget, 3001);
        docs.Add(MakeDoc("STRESS_PARA", "stress_paragraph", 200000, paragraphTarget, paragraph, "single line",
            "one prose paragraph on a single line", quick));
        string table = GenerateStressTable(tableRows, 3002);
        docs.Add(MakeDoc("STRESS_TABLE", "stress_table", 0, table.Length, table, "LF",
            "pipe table with " + tableRows.ToString(CultureInfo.InvariantCulture) + " rows", quick));
        string code = GenerateStressCode(codeLines, 3003);
        docs.Add(MakeDoc("STRESS_CODE", "stress_code", 0, code.Length, code, "LF",
            "single fenced code block with " + codeLines.ToString(CultureInfo.InvariantCulture) + " lines", quick));
        EnsureMediaImages(Path.Combine(outDir, "media"), mediaCount);
        string media = GenerateStressMedia(mediaSections, mediaCount, 4001);
        docs.Add(MakeDoc("STRESS_MEDIA", "stress_media", 0, media.Length, media, "LF",
            "repeating sections of prose, a generated PNG image (media/imgN.png) and display/inline formulas", quick));
        string mermaid = GenerateMermaid(mermaidLines, mermaidBlocks, 5001);
        docs.Add(MakeDoc("MERMAID", "mermaid", 0, mermaid.Length, mermaid, "LF",
            NewlineCount(mermaid).ToString(CultureInfo.InvariantCulture) + " lines, " + FenceCount(mermaid).ToString(CultureInfo.InvariantCulture)
                + " ```mermaid blocks (flowchart, sequence, pie, gantt, mindmap) between prose, lists and one table", quick));
        return docs;
    }

    // "```mermaid" openers in a generated document (the description string is built from this).
    static int FenceCount(string text){
        int count = 0;
        const string opener = "```mermaid\n";
        int at = text.IndexOf(opener, StringComparison.Ordinal);
        while(at >= 0){
            count++;
            at = text.IndexOf(opener, at + opener.Length, StringComparison.Ordinal);
        }
        return count;
    }

    static int ScaleSize(long value, bool quick){
        if(!quick) return (int)value;
        return (int)(value / 100L);
    }

    static int ScaleCount(long value, bool quick){
        if(!quick) return (int)value;
        int scaled = (int)(value / 100L);
        return scaled < 2 ? 2 : scaled;
    }

    static DocData MakeDoc(string name, string kind, int nominal, int target, string text, string newline, string description, bool quick){
        DocData doc = new DocData();
        doc.Name = name;
        doc.Kind = kind;
        doc.NominalChars = nominal;
        doc.TargetChars = target;
        doc.Text = text;
        doc.Characters = text == null ? 0 : text.Length;
        doc.NewlineStyle = newline;
        doc.Description = description;
        doc.QuickScaled = quick;
        return doc;
    }

    static List<DocData> SelectDocs(List<DocData> docs, params string[] names){
        List<DocData> selected = new List<DocData>();
        for(int i = 0; i < names.Length; i++) selected.Add(SelectDoc(docs, names[i]));
        return selected;
    }

    static DocData SelectDoc(List<DocData> docs, string name){
        for(int i = 0; i < docs.Count; i++) if(docs[i].Name == name) return docs[i];
        throw new InvalidOperationException("Generated document not found: " + name);
    }

    static string[] KoreanLines = new string[] {
        "그날 밤 우리는 조용히 창가에 앉아 있었다",
        "오래된 노트의 모서리가 손끝에 닿았다",
        "비가 그친 뒤 골목에는 희미한 빛이 남아 있었다",
        "그는 천천히 고개를 들고 멀리 산등성이를 바라보았다",
        "문장은 짧았지만 의미는 오래 남았다",
        "작은 목소리로 이름을 불렀다",
        "기억은 언제나 예상보다 늦게 도착한다",
        "창문 너머로 바람이 지나가는 소리가 들렸다",
        "우리는 같은 페이지를 여러 번 넘겼다",
        "답장은 없었지만 편지는 계속 쌓였다",
        "새벽의 도시는 낯설 만큼 조용했다",
        "그녀는 웃지 않았고 나도 아무 말을 하지 않았다",
        "책상 위에는 식은 커피와 접힌 지도가 있었다",
        "먼 곳의 이름을 적어 두고 다시 지웠다",
        "내일은 조금 더 천천히 걸어보기로 했다"
    };

    static string[] EnglishLines = new string[] {
        "The rain had stopped before the first train arrived",
        "She wrote the same sentence twice and crossed it out",
        "A quiet room can hold more noise than a crowded street",
        "He kept the old receipt folded inside the notebook",
        "The map was wrong, but nobody wanted to admit it",
        "Morning light moved slowly across the wooden floor",
        "They talked about the weather until the tea went cold",
        "Every draft ended with a question nobody answered",
        "The station clock was three minutes fast again",
        "We walked past the same shop twice without noticing",
        "Her handwriting changed whenever she was tired",
        "The last page was blank, which felt like a small mercy",
        "Outside the window, the river carried the color of the sky",
        "Nobody remembered who had left the light on",
        "Some notes are written only to be reread alone"
    };

    static string[] KoreanWords = new string[] {
        "기억", "바람", "노트", "새벽", "골목", "편지", "창문", "지도", "문장", "커피"
    };

    static string Sentence(Random random){
        string text;
        if(random.Next(100) < 60) text = KoreanLines[random.Next(KoreanLines.Length)];
        else text = EnglishLines[random.Next(EnglishLines.Length)];
        if(random.Next(100) < 4) return text + "!";
        return text + ".";
    }

    static string Decorated(Random random){
        string word = KoreanWords[random.Next(KoreanWords.Length)];
        switch(random.Next(7)){
            case 0: return "**" + word + "**";
            case 1: return "*" + word + "*";
            case 2: return "`cache_" + random.Next(1000).ToString(CultureInfo.InvariantCulture) + "`";
            case 3: return "[참고 자료](https://example.invalid/ref/" + random.Next(1000).ToString(CultureInfo.InvariantCulture) + ")";
            case 4: return Emoji;
            case 5: return "__" + word + "__";
            default: return "**" + word + " " + KoreanWords[random.Next(KoreanWords.Length)] + "**";
        }
    }

    static string ProseLine(Random random){
        StringBuilder line = new StringBuilder();
        int parts = 1 + random.Next(3);
        for(int i = 0; i < parts; i++){
            if(i > 0) line.Append(' ');
            line.Append(Sentence(random));
        }
        if(random.Next(100) < 40){
            line.Append(' ');
            line.Append(Decorated(random));
        }
        if(random.Next(100) < 8) line.Append(' ').Append(Emoji);
        return line.ToString();
    }

    static string ShortCell(Random random){
        string text = random.Next(2) == 0
            ? KoreanLines[random.Next(KoreanLines.Length)]
            : EnglishLines[random.Next(EnglishLines.Length)];
        text = text.Replace("|", "/");
        if(text.Length > 24) text = text.Substring(0, 24);
        return text;
    }

    internal static string GenerateMarkdown(int target, int seed){
        StringBuilder sb = new StringBuilder(target + 4096);
        Random random = new Random(seed);
        string preamble =
            "# MDNote 생성 데이터\n\n" +
            "## 개요\n\n" +
            "### 측정용 문서\n\n" +
            "#### 세부 참고\n\n" +
            "**굵게** *기울임* `code` [링크](https://example.invalid) " + Emoji + " 가 포함된 문단입니다.\n\n" +
            "- 첫 항목\n  - 중첩 항목\n- 둘째 항목\n\n" +
            "1. 첫 번째\n2. 두 번째\n\n" +
            "- [ ] 남은 일\n- [x] 끝난 일\n\n" +
            "> 인용 첫 줄\n> 인용 둘째 줄\n\n" +
            "```text\ncode line one\ncode line two\n```\n\n" +
            "| 번호 | 항목 | 값 |\n|---:|---|---:|\n| 1 | 예시 | 42 |\n\n" +
            "---\n\n";
        sb.Append(preamble);
        while(sb.Length < target) AppendMarkdownBlock(sb, random);
        return sb.ToString();
    }

    static void AppendMarkdownBlock(StringBuilder sb, Random random){
        int roll = random.Next(100);
        if(roll < 42){
            int lines = 1 + random.Next(7);
            for(int i = 0; i < lines; i++) sb.Append(ProseLine(random)).Append('\n');
            sb.Append('\n');
        }else if(roll < 50){
            int level = 1 + random.Next(4);
            sb.Append(new string('#', level)).Append(' ');
            sb.Append(ShortCell(random));
            sb.Append("\n\n");
        }else if(roll < 58){
            int items = 2 + random.Next(7);
            for(int i = 0; i < items; i++){
                if(random.Next(100) < 25) sb.Append("  ");
                sb.Append("- ").Append(ProseLine(random)).Append('\n');
            }
            sb.Append('\n');
        }else if(roll < 65){
            int items = 2 + random.Next(6);
            for(int i = 0; i < items; i++){
                sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(ProseLine(random)).Append('\n');
            }
            sb.Append('\n');
        }else if(roll < 69){
            int items = 2 + random.Next(5);
            for(int i = 0; i < items; i++){
                sb.Append(random.Next(3) == 0 ? "- [x] " : "- [ ] ").Append(ProseLine(random)).Append('\n');
            }
            sb.Append('\n');
        }else if(roll < 76){
            int lines = 1 + random.Next(4);
            for(int i = 0; i < lines; i++) sb.Append("> ").Append(Sentence(random)).Append('\n');
            sb.Append('\n');
        }else if(roll < 84){
            string language = random.Next(3) == 0 ? "csharp" : (random.Next(2) == 0 ? "text" : "javascript");
            sb.Append("```").Append(language).Append('\n');
            int lines = 10 + random.Next(71);
            for(int i = 0; i < lines; i++){
                sb.Append("line ").Append(i + 1).Append(": value_").Append(random.Next(10000));
                sb.Append(" = compute(").Append(random.Next(1000)).Append("); // ");
                sb.Append(EnglishLines[random.Next(EnglishLines.Length)]).Append('\n');
            }
            sb.Append("```\n\n");
        }else if(roll < 88){
            AppendTableBlock(sb, random);
        }else if(roll < 91){
            sb.Append(random.Next(2) == 0 ? "---\n\n" : "***\n\n");
        }else{
            sb.Append('\n');
        }
    }

    static void AppendTableBlock(StringBuilder sb, Random random){
        int columns = 3 + random.Next(3);
        sb.Append('|');
        for(int c = 0; c < columns; c++){
            sb.Append(' ');
            if(c == 0) sb.Append("번호");
            else if(c == 1) sb.Append("항목");
            else if(c == 2) sb.Append("값");
            else sb.Append("열").Append(c + 1);
            sb.Append(" |");
        }
        sb.Append('\n').Append('|');
        for(int c = 0; c < columns; c++){
            if(c == 0) sb.Append("---:");
            else if(c == 1) sb.Append(":---");
            else sb.Append("---:");
            sb.Append('|');
        }
        sb.Append('\n');
        int rows = 5 + random.Next(36);
        for(int r = 0; r < rows; r++){
            sb.Append('|');
            for(int c = 0; c < columns; c++){
                sb.Append(' ');
                if(c == 0) sb.Append(r + 1);
                else if(c == 2) sb.Append(random.Next(100000));
                else sb.Append(ShortCell(random));
                sb.Append(" |");
            }
            sb.Append('\n');
        }
        sb.Append('\n');
    }

    internal static string GeneratePlain(int target, int seed){
        StringBuilder sb = new StringBuilder(target + 4096);
        Random random = new Random(seed);
        while(sb.Length < target){
            int parts = 1 + random.Next(4);
            for(int i = 0; i < parts; i++){
                if(i > 0) sb.Append(' ');
                sb.Append(Sentence(random));
            }
            sb.Append('\n');
        }
        if(sb.Length > target) sb.Length = target;
        return sb.ToString();
    }

    static string GenerateStressParagraph(int target, int seed){
        StringBuilder sb = new StringBuilder(target + 256);
        Random random = new Random(seed);
        while(sb.Length < target){
            if(sb.Length > 0) sb.Append(' ');
            sb.Append(Sentence(random));
        }
        if(sb.Length > target) sb.Length = target;
        return sb.ToString();
    }

    static string GenerateStressTable(int rows, int seed){
        Random random = new Random(seed);
        StringBuilder sb = new StringBuilder(rows * 80 + 128);
        sb.Append("| # | 항목 | 설명 | 값 |\n|---:|---|---|---:|\n");
        for(int r = 0; r < rows; r++){
            sb.Append("| ").Append(r + 1).Append(" | ").Append(ShortCell(random));
            sb.Append(" | ").Append(ShortCell(random)).Append(" | ").Append(random.Next(1000000)).Append(" |\n");
        }
        return sb.ToString();
    }

    static string GenerateStressCode(int lines, int seed){
        Random random = new Random(seed);
        StringBuilder sb = new StringBuilder(lines * 60 + 64);
        sb.Append("```csharp\n");
        for(int i = 0; i < lines; i++){
            sb.Append("var line").Append(i).Append(" = Compute(").Append(random.Next(1000));
            sb.Append("); // stress code ").Append(i).Append('\n');
        }
        sb.Append("```\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- media

    // STRESS_MEDIA draws these generated PNGs through the live preview's own image cache. The
    // document names them relative to the bench editor's BaseDirectory, which RunEditorScenario
    // sets to the output folder (ImageCache.Resolve joins a relative source with that folder).
    static void EnsureMediaImages(string mediaDirectory, int count){
        if(count <= 0) return;
        Directory.CreateDirectory(mediaDirectory);
        for(int i = 1; i <= count; i++){
            string path = Path.Combine(mediaDirectory, "img" + i.ToString(CultureInfo.InvariantCulture) + ".png");
            if(File.Exists(path)) continue;
            WriteMediaImage(path, i);
        }
    }

    // Alternating 2400x1600 and 1200x900: a gradient under random translucent shapes and lines,
    // so the picture is not one flat fill the encoder would shrink to almost nothing.
    static void WriteMediaImage(string path, int index){
        int width = index % 2 == 1 ? 2400 : 1200;
        int height = index % 2 == 1 ? 1600 : 900;
        Random random = new Random(7000 + index);
        DrawingVisual visual = new DrawingVisual();
        using(DrawingContext dc = visual.RenderOpen()){
            Color first = Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            Color second = Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            dc.DrawRectangle(new LinearGradientBrush(first, second, random.Next(360)), null, new Rect(0, 0, width, height));
            int shapes = 24 + random.Next(24);
            for(int i = 0; i < shapes; i++){
                SolidColorBrush brush = new SolidColorBrush(Color.FromArgb((byte)(40 + random.Next(180)),
                    (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
                brush.Freeze();
                double x = random.Next(width);
                double y = random.Next(height);
                double size = 40 + random.Next(width / 4);
                if(random.Next(2) == 0){
                    dc.DrawEllipse(brush, null, new Point(x, y), size, size * (0.3 + random.NextDouble()));
                }else{
                    dc.PushTransform(new RotateTransform(random.Next(360), x, y));
                    dc.DrawRectangle(brush, null, new Rect(x, y, size, size * (0.2 + random.NextDouble())));
                    dc.Pop();
                }
            }
            Pen pen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 2 + random.Next(10));
            pen.Freeze();
            for(int i = 0; i < 12; i++){
                dc.DrawLine(pen, new Point(random.Next(width), random.Next(height)), new Point(random.Next(width), random.Next(height)));
            }
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(FileStream stream = File.Create(path)) encoder.Save(stream);
    }

    // Seventeen lines per section, so 36 sections make a ~600-line document: heading, three
    // prose paragraphs, one picture, a display block whose formula source is two lines (the sum
    // and a matrix) and a paragraph with two inline formulas. Images repeat cyclically.
    static string GenerateStressMedia(int sections, int imageCount, int seed){
        StringBuilder sb = new StringBuilder(sections * 640 + 256);
        Random random = new Random(seed);
        for(int s = 0; s < sections; s++){
            int image = s % imageCount + 1;
            sb.Append("## 미디어 구역 ").Append(s + 1).Append(": 수식과 그림\n\n");
            for(int p = 0; p < 3; p++) sb.Append(ProseLine(random)).Append("\n\n");
            sb.Append("![그림 ").Append(image).Append("](media/img").Append(image).Append(".png)\n\n");
            sb.Append("$$\n");
            sb.Append("\\sum_{i=1}^{n} \\frac{a_i^2}{b_i} = \\int_0^1 f(x)\\,dx\n");
            sb.Append("\\begin{pmatrix} x_{").Append(image).Append("} & y \\\\ z & w \\end{pmatrix}\n");
            sb.Append("$$\n\n");
            sb.Append("인라인 수식 $\\alpha^2 + \\beta^2 = \\gamma^2$ 와 $\\frac{m}{n} \\leq \\sqrt{k+1}$ 가 이 문단에 있습니다.\n\n");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- mermaid

    // The mermaid scenario: a generated document of about `lines` lines with `blocks` ```mermaid
    // blocks (eight flowcharts of 10-15 Korean-labelled nodes, four sequence diagrams, four pies,
    // four gantt charts and four mindmaps at full size) between prose paragraphs, lists and one
    // table. The first block sits a few lines down so it is inside the opening view: the bench waits
    // for the diagrams in view before it starts measuring. Every source is generated here; nothing
    // is read from a file.
    static string GenerateMermaid(int lines, int blocks, int seed){
        Random random = new Random(seed);
        int flow = Math.Max(1, blocks * 8 / 24);
        int sequence = Math.Max(1, blocks / 6);
        int pie = Math.Max(1, blocks / 6);
        int gantt = Math.Max(1, blocks / 6);
        int mind = Math.Max(1, blocks / 6);
        List<string> fences = new List<string>();
        for(int i = 0; i < flow; i++) fences.Add(MermaidFence(MermaidFlowchart(random)));
        for(int i = 0; i < sequence; i++) fences.Add(MermaidFence(MermaidSequence(random)));
        for(int i = 0; i < pie; i++) fences.Add(MermaidFence(MermaidPie(random)));
        for(int i = 0; i < gantt; i++) fences.Add(MermaidFence(MermaidGantt(random)));
        for(int i = 0; i < mind; i++) fences.Add(MermaidFence(MermaidMindmap(random)));
        while(fences.Count > blocks) fences.RemoveAt(fences.Count - 1);
        while(fences.Count < blocks) fences.Add(MermaidFence(MermaidFlowchart(random)));
        Shuffle(fences, random);
        int fenceLines = 0;
        for(int i = 0; i < fences.Count; i++) fenceLines += NewlineCount(fences[i]);
        int gaps = fences.Count + 1;
        // The opening gap stays inside the first viewport (a ~33-line window at 1000x800), so the
        // diagrams the bench waits for before measuring are the first ones in the document.
        int opening = 6;
        int spare = lines - fenceLines - opening;
        if(spare < gaps * 3) spare = gaps * 3;
        int perGap = spare / gaps;
        StringBuilder sb = new StringBuilder(lines * 64 + 4096);
        AppendMermaidFiller(sb, random, opening, false);
        for(int i = 0; i < fences.Count; i++){
            sb.Append(fences[i]);
            AppendMermaidFiller(sb, random, perGap, i == fences.Count / 2);
        }
        for(int guard = 0; guard < 200 && NewlineCount(sb) < lines; guard++) AppendMermaidFiller(sb, random, 4, false);
        return sb.ToString();
    }

    static void AppendMermaidFiller(StringBuilder sb, Random random, int lines, bool withTable){
        int written = 0;
        // Prose paragraphs and bullet lists between the blocks; one pipe table in the middle gap.
        if(withTable){ AppendTableBlock(sb, random); written += 4; }
        while(written < lines - 1){
            if(random.Next(100) < 74){
                int parts = 1 + random.Next(3);
                for(int i = 0; i < parts && written < lines - 1; i++){ sb.Append(ProseLine(random)).Append('\n'); written++; }
                sb.Append('\n');
                written++;
            }else{
                int items = 2 + random.Next(4);
                for(int i = 0; i < items && written < lines - 1; i++){
                    if(random.Next(100) < 25) sb.Append("  ");
                    sb.Append("- ").Append(ProseLine(random)).Append('\n');
                    written++;
                }
                sb.Append('\n');
                written++;
            }
        }
    }

    static void Shuffle(List<string> items, Random random){
        for(int i = items.Count - 1; i > 0; i--){
            int j = random.Next(i + 1);
            string swap = items[i];
            items[i] = items[j];
            items[j] = swap;
        }
    }

    static int NewlineCount(string text){
        int count = 0;
        for(int i = 0; i < text.Length; i++) if(text[i] == '\n') count++;
        return count;
    }

    static int NewlineCount(StringBuilder text){
        int count = 0;
        for(int i = 0; i < text.Length; i++) if(text[i] == '\n') count++;
        return count;
    }

    static string MermaidFence(string source){
        return "```mermaid\n" + source + "```\n\n";
    }

    // 10-15 nodes with Korean labels, a chain through all of them and two shorter crossing edges.
    static string MermaidFlowchart(Random random){
        int nodes = 10 + random.Next(6);
        StringBuilder sb = new StringBuilder("flowchart TD\n");
        for(int i = 0; i < nodes; i++)
            sb.Append("  N").Append(i).Append('[').Append(KoreanWords[i % KoreanWords.Length]).Append(' ').Append(i + 1).Append("]\n");
        for(int i = 0; i + 1 < nodes; i++) sb.Append("  N").Append(i).Append(" --> N").Append(i + 1).Append('\n');
        sb.Append("  N").Append(nodes / 3).Append(" --> N").Append(nodes - 1).Append('\n');
        sb.Append("  N0 --> N").Append(nodes / 2).Append('\n');
        return sb.ToString();
    }

    static string MermaidSequence(Random random){
        StringBuilder sb = new StringBuilder("sequenceDiagram\n  participant 사용자\n  participant 편집기\n  participant 엔진\n");
        int steps = 4 + random.Next(3);
        for(int i = 0; i < steps; i++){
            string message = KoreanLines[random.Next(KoreanLines.Length)];
            if(i % 3 == 0) sb.Append("  사용자->>편집기: ").Append(message).Append('\n');
            else if(i % 3 == 1) sb.Append("  편집기->>엔진: ").Append(message).Append('\n');
            else sb.Append("  엔진-->>사용자: ").Append(message).Append('\n');
        }
        return sb.ToString();
    }

    static string MermaidPie(Random random){
        StringBuilder sb = new StringBuilder("pie title ");
        sb.Append(KoreanWords[random.Next(KoreanWords.Length)]).Append(" 비율\n");
        int slices = 3 + random.Next(3);
        for(int i = 0; i < slices; i++)
            sb.Append("  \"").Append(KoreanWords[random.Next(KoreanWords.Length)]).Append("\" : ").Append(5 + random.Next(60)).Append('\n');
        return sb.ToString();
    }

    static string MermaidGantt(Random random){
        StringBuilder sb = new StringBuilder("gantt\n  title ");
        sb.Append(KoreanWords[random.Next(KoreanWords.Length)]).Append(" 일정\n  dateFormat YYYY-MM-DD\n");
        int sections = 2 + random.Next(2);
        int tasks = 0;
        for(int s = 0; s < sections; s++){
            sb.Append("  section ").Append(KoreanWords[random.Next(KoreanWords.Length)]).Append('\n');
            int count = 2 + random.Next(2);
            for(int t = 0; t < count; t++){
                tasks++;
                sb.Append("  작업").Append(tasks);
                if(tasks == 1) sb.Append(" :g1, 2026-01-05, ").Append(3 + random.Next(6)).Append("d\n");
                else sb.Append(" :g").Append(tasks).Append(", after g").Append(tasks - 1).Append(", ").Append(2 + random.Next(8)).Append("d\n");
            }
        }
        return sb.ToString();
    }

    static string MermaidMindmap(Random random){
        StringBuilder sb = new StringBuilder("mindmap\n  root((");
        sb.Append(KoreanWords[random.Next(KoreanWords.Length)]).Append("))\n");
        int branches = 3 + random.Next(3);
        for(int b = 0; b < branches; b++)
            sb.Append("    ").Append(KoreanWords[random.Next(KoreanWords.Length)]).Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------ scenarios

    static ScenarioGroup RunEditorScenario(string scenarioName, bool withPreview, List<DocData> docs, string baseDirectory){
        ScenarioGroup group = new ScenarioGroup();
        group.Name = scenarioName;
        SourceEditor editor = null;
        Window window = null;
        IDisposable preview = null;
        try{
            editor = CreateEditor();
            if(withPreview){
                try{
                    LivePreviewSpike.Options options = new LivePreviewSpike.Options();
                    options.FontSize = 16.0;
                    options.Family = "Malgun Gothic";
                    options.LinePercent = 150.0;
                    options.Dark = false;
                    options.WordWrap = true;
                    options.BaseDirectory = baseDirectory;
                    preview = LivePreviewSpike.Attach(editor, options);
                }catch(Exception ex){
                    string message = "live preview attach failed: " + Describe(ex);
                    for(int i = 0; i < docs.Count; i++) group.Results.Add(NewSkippedResult(scenarioName, docs[i].Name, "NOT_RUN", message));
                    return group;
                }
            }
            window = CreateWindow(editor);
            window.Show();
            PumpIdleQuiet();

            for(int i = 0; i < docs.Count; i++){
                ScenarioResult result;
                try{
                    result = MeasureEditorDocument(editor, docs[i], scenarioName);
                }catch(Exception ex){
                    result = NewSkippedResult(scenarioName, docs[i].Name, "FAIL", "scenario crashed: " + Describe(ex));
                }
                group.Results.Add(result);
            }
        }catch(Exception ex){
            string message = "scenario setup failed: " + Describe(ex);
            for(int i = group.Results.Count; i < docs.Count; i++) group.Results.Add(NewSkippedResult(scenarioName, docs[i].Name, "FAIL", message));
        }finally{
            if(preview != null){
                try{ preview.Dispose(); }catch{ }
                preview = null;
            }
            if(window != null){
                try{ window.Close(); }catch{ }
                window = null;
            }
            editor = null;
            PumpIdleQuiet();
        }
        return group;
    }

    static ScenarioResult MeasureEditorDocument(SourceEditor editor, DocData doc, string scenarioName){
        ScenarioResult result = new ScenarioResult();
        result.Scenario = scenarioName;
        result.Doc = doc.Name;
        Stopwatch elapsed = Stopwatch.StartNew();
        Watchdog watchdog = new Watchdog(300000.0);
        TextTracker tracker = new TextTracker(doc.Text);
        bool mermaid = doc.Kind == "mermaid";
        // The load stopwatch times the mermaid scenario's first picture (document open included).
        Stopwatch load = mermaid ? Stopwatch.StartNew() : null;
        if(mermaid){
            result.HasMermaid = true;
            result.MermaidRendersBefore = MermaidEngine.DiagRenders;
            result.MermaidStartsBefore = MermaidEngine.DiagEngineStarts;
        }
        try{
            PumpIdleQuiet();
            result.MemoryBeforeOpen = CaptureMemory(false);

            BeginDiagnostics();
            TimingResult open = MeasureAction(delegate{ editor.Text = doc.Text; }, DispatcherPriority.ApplicationIdle, 300000.0, editor.TextArea.TextView);
            EndDiagnostics(scenarioName,doc.Name,"open");
            result.OpenMs = open.Ms;
            result.OpenRenderSeen = open.RenderSeen;
            if(!open.RenderSeen) throw new BenchmarkIncompleteException("open did not reach a rendered frame within 300000 ms");
            watchdog.Check("open");
            result.MemoryAfterOpen = CaptureMemory(false);
            if(mermaid){
                BeginDiagnostics();
                WaitForMermaidPictures(editor, result, load);
                EndDiagnostics(scenarioName,doc.Name,"mermaid-wait");
            }
            if(load != null) load.Stop();

            TimingResult jump = MeasureAction(delegate{ editor.ScrollToEnd(); }, DispatcherPriority.ContextIdle, 120000.0, editor.TextArea.TextView);
            result.JumpEndMs = jump.Ms;
            result.JumpEndRenderSeen = jump.RenderSeen;
            if(!jump.RenderSeen) result.RenderTimeouts++;
            watchdog.Check("jump end");

            RunTyping(editor, result, tracker, watchdog);
            result.MemoryAfterTyping = CaptureMemory(false);
            watchdog.Check("typing");

            RunCaretMoves(editor, result, watchdog);
            // The mermaid scroll samples must measure scrolling over drawn diagrams, not code fences, so
            // a warm-up pass draws every block in view first. The measured scroll below stays unchanged.
            if(mermaid){
                BeginDiagnostics();
                WarmUpMermaidScroll(editor, result, watchdog);
                EndDiagnostics(scenarioName,doc.Name,"mermaid-warm");
            }
            BeginDiagnostics();
            RunScroll(editor, result, watchdog);
            EndDiagnostics(scenarioName,doc.Name,"scroll");

            result.ExpectedLength = tracker.Length;
            result.ActualLength = editor.Document.TextLength;
            result.LengthMatch = result.ExpectedLength == result.ActualLength;
            if(tracker.HasText){
                result.FullTextChecked = true;
                string actual = editor.Document.Text;
                string expected = tracker.Text.ToString();
                result.FullTextMatch = String.Equals(actual, expected, StringComparison.Ordinal);
                if(!result.FullTextMatch) result.FirstDiff = FindFirstDifference(actual, expected);
            }
            if(!result.LengthMatch){
                result.Status = "FAIL";
                result.Error = "text length mismatch: expected " + result.ExpectedLength.ToString(CultureInfo.InvariantCulture)
                    + " actual " + result.ActualLength.ToString(CultureInfo.InvariantCulture);
            }else if(result.FullTextChecked && !result.FullTextMatch){
                result.Status = "FAIL";
                result.Error = "full text mismatch at offset " + result.FirstDiff.ToString(CultureInfo.InvariantCulture);
            }
        }catch(BenchmarkIncompleteException ex){
            result.Status = "INCOMPLETE";
            result.Error = ex.Message;
        }catch(Exception ex){
            result.Status = "FAIL";
            result.Error = Describe(ex);
        }finally{
            result.ElapsedMs = elapsed.Elapsed.TotalMilliseconds;
            if(result.MemoryAfterTyping == null){
                try{ result.MemoryAfterTyping = CaptureMemory(false); }catch{ }
            }
            if(result.ActualLength < 0 && editor.Document != null) result.ActualLength = editor.Document.TextLength;
        }
        return result;
    }

    static void RunTyping(SourceEditor editor, ScenarioResult result, TextTracker tracker, Watchdog watchdog){
        for(int position = 0; position < 3; position++){
            watchdog.Check("typing position");
            PositionResult metrics = new PositionResult();
            metrics.Name = position == 0 ? "start" : (position == 1 ? "middle" : "end");
            int length = editor.Document.TextLength;
            int target = position == 0 ? 0 : (position == 1 ? length / 2 : Math.Max(0, length - 1));
            int lineNumber;
            int lineOffset;
            FindLineStart(editor.Document, target, out lineNumber, out lineOffset);
            metrics.Line = lineNumber;
            metrics.Offset = lineOffset;
            try{ editor.ScrollToLine(lineNumber - 1); }catch{ }
            editor.Select(lineOffset, 0);
            editor.TextArea.Caret.BringCaretToView();
            Settle(editor.TextArea.TextView);

            for(int i = 0; i < 5; i++){
                watchdog.Check("typing warmup");
                int at = editor.TextArea.Caret.Offset;
                TimingResult warmup = MeasureAction(delegate{ editor.TextArea.PerformTextInput("가"); }, DispatcherPriority.ContextIdle, 30000.0, editor.TextArea.TextView);
                tracker.Insert(at, "가");
                if(!warmup.RenderSeen) result.RenderTimeouts++;
            }
            BeginDiagnostics();
            for(int i = 0; i < 50; i++){
                watchdog.Check("typing char");
                int at = editor.TextArea.Caret.Offset;
                TimingResult sample = MeasureAction(delegate{ editor.TextArea.PerformTextInput("가"); }, DispatcherPriority.ContextIdle, 30000.0, editor.TextArea.TextView);
                tracker.Insert(at, "가");
                metrics.CharInsert.Add(sample.Ms, sample.RenderSeen);
                result.MeasuredCharInserts++;
            }
            EndDiagnostics(result.Scenario,result.Doc,"type-"+metrics.Name);
            for(int i = 0; i < 20; i++){
                watchdog.Check("typing enter");
                int at = editor.TextArea.Caret.Offset;
                string newline = NewLineAt(editor.Document, editor.TextArea.Caret.Line);
                TimingResult sample = MeasureAction(delegate{ editor.TextArea.PerformTextInput("\n"); }, DispatcherPriority.ContextIdle, 30000.0, editor.TextArea.TextView);
                tracker.Insert(at, newline);
                metrics.EnterInsert.Add(sample.Ms, sample.RenderSeen);
                result.MeasuredEnterInserts++;
                result.EnterCharsInserted += newline.Length;
            }
            result.Positions.Add(metrics);
        }
    }

    // The mermaid scenario's settling wait: pumps the dispatcher (engine results arrive at Background
    // priority) until every mermaid block in view (one whose opener has a visual line) shows its
    // picture, or the deadline passes. Reports engine renders, the time from the document open until
    // the first in-view picture was drawn, and the working set now (the "after pictures" value the
    // report pairs with the before-open one).
    static void WaitForMermaidPictures(SourceEditor editor, ScenarioResult result, Stopwatch load){
        Stopwatch watch = Stopwatch.StartNew();
        bool settled = false, firstSeen = false;
        double firstPicture = -1;
        while(true){
            PumpIdleQuiet();
            int blocks, folded, inView, shown;
            CountMermaidInView(editor, out blocks, out folded, out inView, out shown);
            result.MermaidBlockCount = blocks;
            result.MermaidFoldCount = folded;
            result.MermaidInView = inView;
            result.MermaidShown = shown;
            if(!firstSeen && shown > 0){
                firstSeen = true;
                if(load != null) firstPicture = load.Elapsed.TotalMilliseconds;
            }
            if(inView > 0 && shown >= inView){ settled = true; break; }
            if(watch.ElapsedMilliseconds >= MermaidWaitMilliseconds) break;
            // No further picture can arrive after the engine gave up; do not hold the bench for the deadline.
            if(MermaidEngine.Unavailable) break;
            Thread.Sleep(10);
        }
        result.MermaidSettled = settled;
        result.MermaidWaitedMs = watch.Elapsed.TotalMilliseconds;
        result.MermaidFirstPictureMs = firstPicture;
        result.MermaidRenders = MermaidEngine.DiagRenders - result.MermaidRendersBefore;
        result.MermaidEngineStarts = MermaidEngine.DiagEngineStarts - result.MermaidStartsBefore;
        result.MermaidUnavailable = MermaidEngine.Unavailable;
        result.MermaidStartupError = MermaidEngine.StartupError;
        result.MermaidLastError = MermaidRender.LastError;
        result.MermaidPictureMemory = CaptureMemory(false);
        PumpIdleQuiet();
    }

    // The mermaid scenario only: scrolls from the top to the bottom in viewport-sized steps, waiting at
    // each step until the diagrams in view are drawn (bounded per step and in total). The measured scroll
    // that follows then runs over drawn, cached pictures instead of code fences. Ends at the top.
    // Drawing a picture above the view changes the height under it, so a single pass can step over a
    // block; the pass repeats on the now settled layout (up to three times) and stops as soon as every
    // block the session built is folded under its picture. Extra passes are cheap: a step with nothing
    // to draw returns at once.
    static void WarmUpMermaidScroll(SourceEditor editor, ScenarioResult result, Watchdog watchdog){
        Stopwatch watch = Stopwatch.StartNew();
        // No block may stay unfolded because the caret sits inside it: the measured scroll starts with
        // every diagram in its picture form.
        try{ editor.Select(0, 0); }catch{ }
        try{ editor.ApplyTemplate(); editor.UpdateLayout(); }catch{ }
        // Typing above left edits behind, and the session rescans a touched block on a 300 ms idle timer.
        // A step returns as soon as the lines in view are fine, which can be milliseconds apart, so pump
        // for a little longer than that timer first; otherwise the pass could cross the document before
        // any rescan runs and every block would stay a code fence.
        Stopwatch settle = Stopwatch.StartNew();
        while(settle.ElapsedMilliseconds < 400){
            PumpIdleQuiet();
            Thread.Sleep(10);
        }
        double viewport = editor.ViewportHeight;
        if(viewport < 1) viewport = 1;
        // Opener line number -> whether that block's picture was seen drawn. A block that missed its
        // step bound is recorded as false here; a later step that draws it overrides it to true, so the
        // count at the end is "in view at least once, never drawn".
        Dictionary<int, bool> drawn = new Dictionary<int, bool>();
        int rendersAtStart = MermaidEngine.DiagRenders;
        int steps = 0, passes = 0;
        bool cutShort = false;
        int blocks = 0, folded = 0, inView = 0, shown = 0;
        while(passes < 3){
            passes++;
            double offset = 0;
            while(true){
                watchdog.Check("mermaid warm-up");
                if(watch.ElapsedMilliseconds >= MermaidWarmTotalMilliseconds){ cutShort = true; break; }
                PumpIdleQuiet();
                try{ editor.ScrollToVerticalOffset(offset); }catch{ }
                WaitForMermaidStep(editor, drawn, watchdog);
                steps++;
                // No further picture can arrive once the engine gave up; do not walk the rest of the document.
                if(MermaidEngine.Unavailable) break;
                double extent = editor.ExtentHeight;
                double maximum = extent - editor.ViewportHeight;
                if(maximum < 0) maximum = 0;
                if(offset >= maximum) break;
                offset += viewport;
                if(offset > maximum) offset = maximum;
            }
            // Back to the top: the existing scroll measurement starts there, over the drawn pictures.
            try{ editor.ScrollToVerticalOffset(0); }catch{ }
            PumpIdleQuiet();
            CountMermaidInView(editor, drawn, out blocks, out folded, out inView, out shown);
            if(cutShort || MermaidEngine.Unavailable) break;
            // Every block the session built is folded under its picture: nothing is left for a pass.
            if(blocks == 0 || folded >= blocks) break;
            if(watch.ElapsedMilliseconds >= MermaidWarmTotalMilliseconds){ cutShort = true; break; }
        }
        result.MermaidWarmMs = watch.Elapsed.TotalMilliseconds;
        result.MermaidWarmSteps = steps;
        result.MermaidWarmPasses = passes;
        result.MermaidWarmCutShort = cutShort;
        // Renders during this pass, and the total since the document opened (the number that says every
        // block has been drawn). Renders made while typing and moving the caret in between are in neither.
        result.MermaidWarmRenders = MermaidEngine.DiagRenders - rendersAtStart;
        result.MermaidWarmRendersTotal = MermaidEngine.DiagRenders - result.MermaidRendersBefore;
        result.MermaidWarmBlocks = blocks;
        result.MermaidWarmFolded = folded;
        // In view at least once during the pass, never drawn: each such block failed its step bound.
        int undrawn = 0;
        foreach(bool seen in drawn.Values) if(!seen) undrawn++;
        result.MermaidWarmUndrawn = undrawn;
    }

    // One warm-up step: pumps the dispatcher until every mermaid block in view shows its picture, the
    // per-step bound passes, or the engine gave up. Every opener in view is recorded in `drawn` while the
    // step runs (false = still not drawn), so a step that gives up leaves its blocks marked.
    static void WaitForMermaidStep(SourceEditor editor, Dictionary<int, bool> drawn, Watchdog watchdog){
        Stopwatch watch = Stopwatch.StartNew();
        while(true){
            watchdog.Check("mermaid warm-up step");
            PumpIdleQuiet();
            int blocks, folded, inView, shown;
            CountMermaidInView(editor, drawn, out blocks, out folded, out inView, out shown);
            if(inView == 0 || shown >= inView) return;
            if(watch.ElapsedMilliseconds >= MermaidWarmStepMilliseconds || MermaidEngine.Unavailable) return;
            Thread.Sleep(10);
        }
    }

    // A mermaid block is "in view" when its opener line has a visual line on screen (a folded block's
    // opener is drawn; the source lines under it are folded away). Shown = that visual line carries a
    // MermaidElement, i.e. the block is drawn as its picture now.
    static void CountMermaidInView(SourceEditor editor, out int blocks, out int folded, out int inView, out int shown){
        CountMermaidInView(editor, null, out blocks, out folded, out inView, out shown);
    }

    // With `drawn` not null, every opener in view is recorded there by line number: true when its
    // picture is drawn now, false when it is not (the warm-up's record of blocks that missed a step).
    static void CountMermaidInView(SourceEditor editor, Dictionary<int, bool> drawn, out int blocks, out int folded, out int inView, out int shown){
        blocks = folded = inView = shown = 0;
        MarkdownLivePreview.Session session = MarkdownLivePreview.SessionFor(editor);
        if(session == null) return;
        blocks = session.MermaidBlockCount;
        folded = session.MermaidFoldCount;
        TextView view = editor.TextArea.TextView;
        try{ view.EnsureVisualLines(); }catch(Exception){ return; }
        if(!view.VisualLinesValid) return;
        foreach(VisualLine visual in view.VisualLines){
            if(visual == null || visual.IsDisposed) continue;
            DocumentLine opener = visual.FirstDocumentLine;
            string text = editor.Document.GetText(opener.Offset, opener.Length);
            if(!MermaidRender.IsMermaidFence(text)) continue;
            inView++;
            bool isDrawn = HasMermaidElement(visual);
            if(isDrawn) shown++;
            if(drawn == null) continue;
            // A block first seen undrawn and drawn later counts as drawn.
            if(isDrawn || !drawn.ContainsKey(opener.LineNumber)) drawn[opener.LineNumber] = isDrawn;
        }
    }

    static bool HasMermaidElement(VisualLine visual){
        IList<VisualLineElement> elements = visual.Elements;
        for(int i = 0; i < elements.Count; i++){
            MermaidElement diagram = elements[i] as MermaidElement;
            if(diagram != null && diagram.Picture != null) return true;
        }
        return false;
    }

    static void RunCaretMoves(SourceEditor editor, ScenarioResult result, Watchdog watchdog){
        try{ editor.ScrollToLine(0); }catch{ }
        editor.Select(0, 0);
        editor.TextArea.Caret.BringCaretToView();
        Settle(editor.TextArea.TextView);
        for(int i = 0; i < 50; i++){
            watchdog.Check("caret move");
            TimingResult sample = MeasureAction(delegate{
                int before = editor.TextArea.Caret.Line;
                try{ EditingCommands.MoveDownByLine.Execute(null, editor.TextArea); }catch{ }
                if(editor.TextArea.Caret.Line == before){
                    int next = before + 1;
                    if(next > editor.Document.LineCount) next = 1;
                    editor.TextArea.Caret.Line = next;
                    editor.TextArea.Caret.BringCaretToView();
                }
            }, DispatcherPriority.ContextIdle, 30000.0, editor.TextArea.TextView);
            result.CaretMoves.Add(sample.Ms, sample.RenderSeen);
        }
    }

    static void RunScroll(SourceEditor editor, ScenarioResult result, Watchdog watchdog){
        try{ editor.ApplyTemplate(); editor.UpdateLayout(); }catch{ }
        double extent = editor.ExtentHeight;
        double viewport = editor.ViewportHeight;
        result.ScrollExtent = extent;
        result.ScrollViewport = viewport;
        double maximum = extent - viewport;
        if(maximum < 0) maximum = 0;
        for(int i = 0; i < 40; i++){
            watchdog.Check("scroll");
            double offset = i == 39 ? maximum : maximum * (i / 39.0);
            BeginDiagnostics();
            TimingResult sample = MeasureAction(delegate{ editor.ScrollToVerticalOffset(offset); }, DispatcherPriority.ContextIdle, 30000.0, editor.TextArea.TextView);
            EndDiagnostics(result.Scenario,result.Doc,"scroll-"+i+"-ms"+sample.Ms.ToString("F3",CultureInfo.InvariantCulture));
            result.Scroll.Add(sample.Ms, sample.RenderSeen);
        }
    }

    // ------------------------------------------------------------ measurement

    static TimingResult MeasureAction(Action action, DispatcherPriority idlePriority, double timeoutMilliseconds, UIElement invalidate){
        if(invalidate != null) invalidate.InvalidateVisual();
        Stopwatch watch = Stopwatch.StartNew();
        DispatcherFrame frame = new DispatcherFrame();
        bool renderSeen = false;

        EventHandler rendering = null;
        rendering = delegate{
            if(renderSeen) return;
            renderSeen = true;
            CompositionTarget.Rendering -= rendering;
            Dispatcher.CurrentDispatcher.BeginInvoke(idlePriority, new Action(delegate{ frame.Continue = false; }));
        };
        DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Send);
        timer.Interval = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        EventHandler tick = null;
        tick = delegate{
            timer.Stop();
            frame.Continue = false;
        };
        timer.Tick += tick;
        CompositionTarget.Rendering += rendering;
        timer.Start();
        try{
            action();
            Dispatcher.PushFrame(frame);
        }finally{
            timer.Stop();
            timer.Tick -= tick;
            CompositionTarget.Rendering -= rendering;
        }
        watch.Stop();
        TimingResult result = new TimingResult();
        result.Ms = watch.Elapsed.TotalMilliseconds;
        result.RenderSeen = renderSeen;
        result.TimedOut = !renderSeen;
        return result;
    }

    static void Settle(UIElement invalidate){
        MeasureAction(delegate{ }, DispatcherPriority.ContextIdle, 30000.0, invalidate);
    }

    static void PumpIdleQuiet(){
        try{
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate{ frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }catch{ }
    }

    static ScenarioResult NewSkippedResult(string scenario, string doc, string status, string error){
        ScenarioResult result = new ScenarioResult();
        result.Scenario = scenario;
        result.Doc = doc;
        result.Status = status;
        result.Error = error;
        return result;
    }

    static SourceEditor CreateEditor(){
        SourceEditor editor = new SourceEditor();
        editor.FontFamily = new FontFamily("Malgun Gothic");
        editor.FontSize = 16.0;
        editor.WordWrap = true;
        editor.TextArea.TextView.LineHeight = 16.0 * 1.5;
        return editor;
    }

    static Window CreateWindow(UIElement content){
        Window window = new Window();
        window.Left = -20000;
        window.Top = 0;
        double width;
        double height;
        BenchWindowSize(out width, out height);
        window.Width = width;
        window.Height = height;
        // On screen in a full run (VM only), so the compositor really presents every frame.
        if(BenchFullscreen()){ Rect workArea = SystemParameters.WorkArea; window.Left = workArea.Left; window.Top = workArea.Top; }
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Content = content;
        return window;
    }

    // A full run can ask for the primary screen's work area so the measured frame matches the
    // 1440p / 4K resolution instead of the default 1000x800 window.
    static bool BenchFullscreen(){
        return String.Equals(Environment.GetEnvironmentVariable("MDNOTE_BENCH_FULLSCREEN"), "1", StringComparison.Ordinal);
    }

    static void BenchWindowSize(out double width, out double height){
        width = 1000;
        height = 800;
        // MDNOTE_BENCH_SIZE=WxH: a large window kept off screen (the user's PC stays untouched).
        string size = Environment.GetEnvironmentVariable("MDNOTE_BENCH_SIZE");
        if(!String.IsNullOrEmpty(size)){
            string[] parts = size.Split('x');
            double w, h;
            if(parts.Length == 2 && Double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out w) && Double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) && w >= 100 && h >= 100){ width = w; height = h; return; }
        }
        if(!BenchFullscreen()) return;
        try{
            Rect workArea = SystemParameters.WorkArea;
            if(workArea.Width >= 1 && workArea.Height >= 1){
                width = workArea.Width;
                height = workArea.Height;
            }
        }catch(Exception){ }
    }

    static void FindLineStart(TextDocument document, int targetOffset, out int lineNumber, out int lineOffset){
        if(targetOffset < 0) targetOffset = 0;
        if(targetOffset > document.TextLength) targetOffset = document.TextLength;
        int startLine = document.GetLineByOffset(targetOffset).LineNumber;

        for(int distance = 0; distance <= 250; distance++){
            int forward = startLine + distance;
            if(forward >= 1 && forward <= document.LineCount){
                DocumentLine line = document.GetLineByNumber(forward);
                if(IsProseLine(document.GetText(line.Offset, line.Length))){
                    lineNumber = forward;
                    lineOffset = line.Offset;
                    return;
                }
            }
            if(distance > 0){
                int backward = startLine - distance;
                if(backward >= 1){
                    DocumentLine line = document.GetLineByNumber(backward);
                    if(IsProseLine(document.GetText(line.Offset, line.Length))){
                        lineNumber = backward;
                        lineOffset = line.Offset;
                        return;
                    }
                }
            }
        }
        for(int distance = 0; distance <= 250; distance++){
            int forward = startLine + distance;
            if(forward >= 1 && forward <= document.LineCount){
                DocumentLine line = document.GetLineByNumber(forward);
                if(!String.IsNullOrWhiteSpace(document.GetText(line.Offset, line.Length))){
                    lineNumber = forward;
                    lineOffset = line.Offset;
                    return;
                }
            }
            if(distance > 0){
                int backward = startLine - distance;
                if(backward >= 1){
                    DocumentLine line = document.GetLineByNumber(backward);
                    if(!String.IsNullOrWhiteSpace(document.GetText(line.Offset, line.Length))){
                        lineNumber = backward;
                        lineOffset = line.Offset;
                        return;
                    }
                }
            }
        }
        DocumentLine fallback = document.GetLineByNumber(startLine);
        lineNumber = startLine;
        lineOffset = fallback.Offset;
    }

    static bool IsProseLine(string text){
        if(String.IsNullOrEmpty(text)) return false;
        string trimmed = text.TrimStart();
        if(trimmed.Length == 0) return false;
        // An indented line is never picked: a caret at its start that takes an Enter has its new
        // line's leading spaces removed (the product's own Enter rule), which this harness's simple
        // tracker does not model - the check would fail on correct behaviour.
        if(trimmed.Length != text.Length) return false;
        char first = trimmed[0];
        if(first == '#' || first == '>' || first == '|' || first == '`' || first == '~') return false;
        if(first == '-' || first == '*' || first == '+' || first == '_') return false;
        if(first >= '0' && first <= '9'){
            int i = 0;
            while(i < trimmed.Length && Char.IsDigit(trimmed[i])) i++;
            if(i + 1 < trimmed.Length && (trimmed[i] == '.' || trimmed[i] == ')') && trimmed[i + 1] == ' ') return false;
        }
        return Char.IsLetter(first) || first == '"' || first == '\'' || first == '(';
    }

    static string NewLineAt(TextDocument document, int lineNumber){
        if(lineNumber < 1) lineNumber = 1;
        if(lineNumber > document.LineCount) lineNumber = document.LineCount;
        DocumentLine line = document.GetLineByNumber(lineNumber);
        if(line.DelimiterLength == 0){
            line = line.PreviousLine;
            if(line == null) return Environment.NewLine;
        }
        return document.GetText(line.Offset + line.Length, line.DelimiterLength);
    }

    static long FindFirstDifference(string actual, string expected){
        int limit = Math.Min(actual.Length, expected.Length);
        for(int i = 0; i < limit; i++) if(actual[i] != expected[i]) return i;
        return limit;
    }

    // ------------------------------------------------------------ output

    static void WriteOutputs(string outDir, List<DocData> docs, List<ScenarioGroup> groups, EnvironmentInfo environment,
        DateTime timestamp, bool quick, double elapsedMilliseconds, string fatal, MemorySnapshot finalDiagnostic){
        string json = BuildJson(docs, groups, environment, timestamp, quick, elapsedMilliseconds, fatal, finalDiagnostic);
        string markdown = BuildMarkdown(docs, groups, environment, timestamp, quick, elapsedMilliseconds, fatal);
        File.WriteAllText(Path.Combine(outDir, "layout-counters.tsv"), diagnostics.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "summary.json"), json, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "summary.md"), markdown, new UTF8Encoding(false));
    }

    static string BuildJson(List<DocData> docs, List<ScenarioGroup> groups, EnvironmentInfo environment,
        DateTime timestamp, bool quick, double elapsedMilliseconds, string fatal, MemorySnapshot finalDiagnostic){
        JsonOut json = new JsonOut();
        json.BeginObject();
        json.StringProperty("harness", "LivePreviewBench");
        json.IntegerProperty("format_version", 1);
        json.StringProperty("timestamp", timestamp.ToString("o", CultureInfo.InvariantCulture));
        json.BoolProperty("quick", quick);
        json.StringProperty("status", fatal == null ? "COMPLETE" : "HARNESS_FAILURE");
        if(fatal != null) json.StringProperty("fatal_error", fatal);

        json.Name("environment");
        WriteEnvironment(json, environment);

        json.Name("data");
        json.BeginArray();
        if(docs != null){
            for(int i = 0; i < docs.Count; i++){
                DocData doc = docs[i];
                json.BeginObject();
                json.StringProperty("name", doc.Name);
                json.StringProperty("kind", doc.Kind);
                json.IntegerProperty("nominal_chars", doc.NominalChars);
                json.IntegerProperty("target_chars", doc.TargetChars);
                json.IntegerProperty("actual_chars", doc.Characters);
                json.StringProperty("newline", doc.NewlineStyle);
                json.BoolProperty("quick_scaled", doc.QuickScaled);
                json.StringProperty("description", doc.Description);
                json.EndObject();
            }
        }
        json.EndArray();

        json.Name("scenarios");
        json.BeginArray();
        if(groups != null){
            for(int i = 0; i < groups.Count; i++){
                ScenarioGroup group = groups[i];
                json.BeginObject();
                json.StringProperty("scenario", group.Name);
                json.Name("docs");
                json.BeginArray();
                for(int j = 0; j < group.Results.Count; j++) WriteResultJson(json, group.Results[j]);
                json.EndArray();
                json.EndObject();
            }
        }
        json.EndArray();

        json.Name("final_after_forced_gc_diagnostic");
        WriteMemory(json, finalDiagnostic);
        json.NumberProperty("elapsed_ms", elapsedMilliseconds);
        json.EndObject();
        return json.Buffer.ToString();
    }

    static void WriteEnvironment(JsonOut json, EnvironmentInfo environment){
        if(environment == null){
            json.NullValue();
            return;
        }
        json.BeginObject();
        json.IntegerProperty("processor_count", environment.ProcessorCount);
        json.StringProperty("os_version", environment.OsVersion);
        json.BoolProperty("is_64_bit_process", environment.Is64Bit);
        json.IntegerProperty("primary_screen_width", environment.ScreenWidth);
        json.IntegerProperty("primary_screen_height", environment.ScreenHeight);
        json.NumberProperty("primary_screen_dpi_x", environment.DpiX);
        json.NumberProperty("primary_screen_dpi_y", environment.DpiY);
        json.NumberProperty("bench_window_width", environment.BenchWindowWidth);
        json.NumberProperty("bench_window_height", environment.BenchWindowHeight);
        json.BoolProperty("bench_window_fullscreen", environment.BenchWindowFullscreen);
        json.StringProperty("process_render_mode", environment.ProcessRenderMode);
        json.StringProperty("exe_path", environment.ExePath);
        json.StringProperty("exe_sha256", environment.ExeSha256);
        json.StringProperty("run_timestamp", environment.Timestamp);
        json.IntegerProperty("run_timestamp_unix_ms", environment.TimestampUnixMs);
        json.EndObject();
    }

    static void WriteResultJson(JsonOut json, ScenarioResult result){
        json.BeginObject();
        json.StringProperty("scenario", result.Scenario);
        json.StringProperty("doc", result.Doc);
        json.StringProperty("status", result.Status);
        json.StringProperty("error", result.Error);
        json.NumberProperty("elapsed_ms", result.ElapsedMs);
        json.NumberProperty("open_ms", result.OpenMs);
        json.BoolProperty("open_render_seen", result.OpenRenderSeen);
        json.NumberProperty("jump_end_ms", result.JumpEndMs);
        json.BoolProperty("jump_end_render_seen", result.JumpEndRenderSeen);
        json.NumberProperty("parse_ms", result.ParseMs);
        json.IntegerProperty("render_timeouts", result.RenderTimeouts);

        json.Name("typing");
        json.BeginObject();
        for(int i = 0; i < result.Positions.Count; i++){
            PositionResult position = result.Positions[i];
            json.Name(position.Name);
            json.BeginObject();
            json.Name("char");
            WriteSamples(json, position.CharInsert);
            json.Name("enter");
            WriteSamples(json, position.EnterInsert);
            json.IntegerProperty("line", position.Line);
            json.IntegerProperty("offset", position.Offset);
            json.EndObject();
        }
        json.EndObject();

        json.Name("caret_moves");
        WriteSamples(json, result.CaretMoves);

        json.Name("scroll");
        json.BeginObject();
        json.NumberProperty("extent_height", result.ScrollExtent);
        json.NumberProperty("viewport_height", result.ScrollViewport);
        json.Name("samples");
        WriteSamples(json, result.Scroll);
        json.EndObject();

        json.Name("memory");
        json.BeginObject();
        json.Name("before_open");
        WriteMemory(json, result.MemoryBeforeOpen);
        json.Name("after_open");
        WriteMemory(json, result.MemoryAfterOpen);
        json.Name("after_typing");
        WriteMemory(json, result.MemoryAfterTyping);
        json.EndObject();

        if(result.HasMermaid) WriteMermaidJson(json, result);

        json.Name("correctness");
        json.BeginObject();
        json.IntegerProperty("expected_text_length", result.ExpectedLength);
        json.IntegerProperty("actual_text_length", result.ActualLength);
        json.BoolProperty("length_match", result.LengthMatch);
        json.BoolProperty("full_text_checked", result.FullTextChecked);
        json.BoolProperty("full_text_match", result.FullTextMatch);
        json.IntegerProperty("first_difference", result.FirstDiff);
        json.IntegerProperty("measured_char_inserts", result.MeasuredCharInserts);
        json.IntegerProperty("measured_enter_inserts", result.MeasuredEnterInserts);
        json.IntegerProperty("enter_newline_chars", result.EnterCharsInserted);
        json.EndObject();

        json.EndObject();
    }

    // The mermaid scenario's own block: what the settling wait saw, what the warm-up pass before the
    // scroll samples reached, and the working set after the pictures were drawn. Other scenarios do not
    // write this object at all.
    static void WriteMermaidJson(JsonOut json, ScenarioResult result){
        json.Name("mermaid");
        json.BeginObject();
        json.BoolProperty("settled", result.MermaidSettled);
        json.NumberProperty("wait_ms", result.MermaidWaitedMs);
        json.NumberProperty("first_picture_ms", result.MermaidFirstPictureMs);
        json.IntegerProperty("engine_starts", result.MermaidEngineStarts);
        json.IntegerProperty("engine_renders", result.MermaidRenders);
        json.Name("warm_up");
        json.BeginObject();
        json.NumberProperty("ms", result.MermaidWarmMs);
        json.IntegerProperty("steps", result.MermaidWarmSteps);
        json.IntegerProperty("passes", result.MermaidWarmPasses);
        json.IntegerProperty("engine_renders_total", result.MermaidWarmRendersTotal);
        json.IntegerProperty("engine_renders_during_warm_up", result.MermaidWarmRenders);
        json.IntegerProperty("block_count", result.MermaidWarmBlocks);
        json.IntegerProperty("fold_count", result.MermaidWarmFolded);
        json.IntegerProperty("undrawn_blocks", result.MermaidWarmUndrawn);
        json.BoolProperty("cut_short", result.MermaidWarmCutShort);
        json.EndObject();
        json.BoolProperty("engine_unavailable", result.MermaidUnavailable);
        json.StringProperty("engine_startup_error", result.MermaidStartupError);
        json.StringProperty("last_error", result.MermaidLastError);
        json.IntegerProperty("block_count", result.MermaidBlockCount);
        json.IntegerProperty("fold_count", result.MermaidFoldCount);
        json.IntegerProperty("in_view", result.MermaidInView);
        json.IntegerProperty("shown", result.MermaidShown);
        json.Name("after_pictures");
        WriteMemory(json, result.MermaidPictureMemory);
        json.EndObject();
    }

    static void WriteSamples(JsonOut json, Samples samples){
        json.BeginObject();
        json.IntegerProperty("count", samples.Count);
        json.NumberProperty("p50", samples.P50);
        json.NumberProperty("p95", samples.P95);
        json.NumberProperty("p99", samples.P99);
        json.NumberProperty("max", samples.Max);
        json.IntegerProperty("over_200_ms", samples.Over200);
        json.IntegerProperty("render_timeouts", samples.RenderTimeouts);
        json.EndObject();
    }

    static void WriteMemory(JsonOut json, MemorySnapshot memory){
        if(memory == null){
            json.NullValue();
            return;
        }
        json.BeginObject();
        json.IntegerProperty("private_bytes", memory.PrivateBytes);
        json.IntegerProperty("working_set_bytes", memory.WorkingSetBytes);
        json.IntegerProperty("managed_bytes", memory.ManagedBytes);
        json.EndObject();
    }

    static string BuildMarkdown(List<DocData> docs, List<ScenarioGroup> groups, EnvironmentInfo environment,
        DateTime timestamp, bool quick, double elapsedMilliseconds, string fatal){
        StringBuilder sb = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;
        sb.Append("# LivePreviewBench\n\n");
        sb.Append("- Timestamp: ").Append(timestamp.ToString("o", inv)).Append('\n');
        sb.Append("- Quick mode (sizes / 100): ").Append(quick ? "true" : "false").Append('\n');
        if(environment != null){
            sb.Append("- CPU: ").Append(environment.ProcessorCount).Append(" | OS: ").Append(environment.OsVersion);
            sb.Append(" | 64-bit: ").Append(environment.Is64Bit ? "true" : "false");
            sb.Append(" | DPI: ").Append(Format2(environment.DpiX)).Append('x').Append(Format2(environment.DpiY)).Append('\n');
            sb.Append("- Bench window: ").Append(Format2(environment.BenchWindowWidth)).Append('x').Append(Format2(environment.BenchWindowHeight));
            sb.Append(" | MDNOTE_BENCH_FULLSCREEN: ").Append(environment.BenchWindowFullscreen ? "1" : "not set");
            sb.Append(" | process render mode: ").Append(environment.ProcessRenderMode).Append('\n');
            sb.Append("- Exe SHA-256: ").Append(environment.ExeSha256).Append('\n');
        }
        sb.Append("- Harness: ").Append(fatal == null ? "COMPLETE" : "HARNESS_FAILURE");
        sb.Append(" | elapsed ").Append(FormatMs(elapsedMilliseconds)).Append(" ms\n\n");

        if(docs != null && docs.Count > 0){
            sb.Append("Generated data: ");
            for(int i = 0; i < docs.Count; i++){
                if(i > 0) sb.Append(", ");
                sb.Append(docs[i].Name).Append("=").Append(docs[i].Characters.ToString(inv));
            }
            sb.Append("\n\n");
        }

        sb.Append("| Scenario | Doc | Open ms | Jump end ms | Type p50 (S/M/E) | Type p95 (S/M/E) | Type p99 (S/M/E) | Type max (S/M/E) | Scroll p95 | Private MB | Status |\n");
        sb.Append("|---|---|---:|---:|---|---|---|---|---:|---:|---|\n");
        for(int g = 0; g < groups.Count; g++){
            ScenarioGroup group = groups[g];
            for(int i = 0; i < group.Results.Count; i++){
                ScenarioResult result = group.Results[i];
                sb.Append("| ").Append(result.Scenario);
                sb.Append(" | ").Append(result.Doc);
                sb.Append(" | ").Append(FormatMs(result.OpenMs));
                sb.Append(" | ").Append(FormatMs(result.JumpEndMs));
                sb.Append(" | ").Append(StatTriple(result, "p50"));
                sb.Append(" | ").Append(StatTriple(result, "p95"));
                sb.Append(" | ").Append(StatTriple(result, "p99"));
                sb.Append(" | ").Append(StatTriple(result, "max"));
                sb.Append(" | ").Append(result.Scroll.Count == 0 ? "-" : FormatMs(result.Scroll.P95));
                sb.Append(" | ").Append(PrivateMb(result));
                sb.Append(" | ").Append(result.Status);
                sb.Append(" |\n");
            }
        }
        if(groups.Count == 0) sb.Append("| - | - | - | - | - | - | - | - | - | - | - |\n");
        sb.Append('\n');

        // The mermaid scenario's own numbers (other scenarios write nothing here).
        bool mermaidHeader = false;
        for(int g = 0; g < groups.Count; g++){
            for(int i = 0; i < groups[g].Results.Count; i++){
                ScenarioResult result = groups[g].Results[i];
                if(!result.HasMermaid) continue;
                if(!mermaidHeader){
                    sb.Append("## Mermaid scenario\n\n");
                    string header = "| Scenario | Doc | Blocks (folded) | In view (shown) | Settled | Wait ms | First picture ms | Engine starts | Engine renders | Warm-up ms (steps) | Warm-up renders (pass/total) | Blocks (folded) after warm-up | Undrawn after warm-up | Cut short | Working set before load | Working set after pictures | View | Engine |\n";
                    sb.Append(header);
                    // Built from the header, and aligned like the old hand-written rule: numbers right,
                    // "View"/"Engine" left. A column added to one row and not the other makes markdown
                    // draw the rows as plain text, which a counted row avoids.
                    sb.Append(TableRule(header, "llrllrrrrrrrrlrrll"));
                    mermaidHeader = true;
                }
                sb.Append("| ").Append(result.Scenario);
                sb.Append(" | ").Append(result.Doc);
                sb.Append(" | ").Append(result.MermaidBlockCount).Append(" (").Append(result.MermaidFoldCount).Append(")");
                sb.Append(" | ").Append(result.MermaidInView).Append(" (").Append(result.MermaidShown).Append(")");
                sb.Append(" | ").Append(result.MermaidSettled ? "yes" : "no");
                sb.Append(" | ").Append(FormatMs(result.MermaidWaitedMs));
                sb.Append(" | ").Append(FormatMs(result.MermaidFirstPictureMs));
                sb.Append(" | ").Append(result.MermaidEngineStarts);
                sb.Append(" | ").Append(result.MermaidRenders);
                sb.Append(" | ").Append(FormatMs(result.MermaidWarmMs)).Append(" (").Append(result.MermaidWarmSteps).Append(" steps, ").Append(result.MermaidWarmPasses).Append(" passes)");
                sb.Append(" | ").Append(result.MermaidWarmRenders).Append(" (total ").Append(result.MermaidWarmRendersTotal).Append(")");
                sb.Append(" | ").Append(result.MermaidWarmBlocks).Append(" (").Append(result.MermaidWarmFolded).Append(")");
                sb.Append(" | ").Append(result.MermaidWarmUndrawn);
                sb.Append(" | ").Append(result.MermaidWarmCutShort ? "yes" : "no");
                sb.Append(" | ").Append(WorkingSetMb(result.MemoryBeforeOpen));
                sb.Append(" | ").Append(WorkingSetMb(result.MermaidPictureMemory));
                sb.Append(" | ").Append(HideMarkersInEffect() ? "basic (markers hidden)" : "live");
                sb.Append(" | ").Append(result.MermaidUnavailable ? "unavailable: " + Shorten(result.MermaidStartupError, 120) : "ok");
                sb.Append(" |\n");
            }
        }
        if(mermaidHeader){
            sb.Append('\n');
            sb.Append("- Warm-up: before the scroll samples the bench scrolls the document in viewport steps and waits until the diagrams in view are drawn (5 s per step, 150 s total), then returns to the top. The Scroll p95 in the table above is therefore scrolling over drawn, cached pictures.\n\n");
        }

        bool anyProblem = false;
        for(int g = 0; g < groups.Count; g++){
            for(int i = 0; i < groups[g].Results.Count; i++){
                ScenarioResult result = groups[g].Results[i];
                if(result.Status != "PASS"){
                    if(!anyProblem){
                        sb.Append("## Non-PASS rows\n\n");
                        anyProblem = true;
                    }
                    sb.Append("- ").Append(result.Scenario).Append(" / ").Append(result.Doc).Append(": ").Append(result.Status);
                    if(!String.IsNullOrEmpty(result.Error)) sb.Append(" — ").Append(Shorten(result.Error, 240));
                    sb.Append('\n');
                }
            }
        }
        if(fatal != null){
            sb.Append("\nHarness failure: ").Append(Shorten(fatal, 600)).Append('\n');
        }
        sb.Append("\n## Method notes\n\n");
        sb.Append("- Synthetic data only; no user files are read.\n");
        sb.Append("- Each sample waits for the next CompositionTarget.Rendering frame, then dispatcher ContextIdle (open waits for ApplicationIdle).\n");
        sb.Append("- Windows are offscreen; insertions are synthetic AvalonEdit PerformTextInput calls.\n");
        sb.Append("- Memory values are raw process/GC values; the synthetic data set remains live during scenarios. Forced GC is only the labeled final diagnostic.\n");
        return sb.ToString();
    }

    // The "|---|" rule line of a markdown table, one cell per "|" of the header row (the mermaid table
    // grows often, and a miscounted rule line makes the table render as plain text). `align` gives one
    // character per column: 'r' right, 'c' centre, anything else (or a shorter string) plain. A markdown
    // rule line "---:" also works without the leading dashes, so only the colon is added.
    static string TableRule(string header, string align){
        StringBuilder rule = new StringBuilder();
        int pipes = 0;
        for(int i = 0; i < header.Length; i++) if(header[i] == '|') pipes++;
        int cells = pipes - 1;
        if(cells < 1) return "";
        for(int i = 0; i < cells; i++){
            char a = align != null && i < align.Length ? align[i] : 'l';
            if(a == 'r') rule.Append("---:|");
            else if(a == 'c') rule.Append(":---:|");
            else rule.Append("---|");
        }
        rule.Append('\n');
        return rule.ToString();
    }

    static string StatTriple(ScenarioResult result, string statistic){
        string[] values = new string[3];
        for(int i = 0; i < 3; i++){
            PositionResult position = FindPosition(result, i == 0 ? "start" : (i == 1 ? "middle" : "end"));
            if(position == null || position.CharInsert.Count == 0) values[i] = "-";
            else values[i] = FormatMs(Statistic(position.CharInsert, statistic));
        }
        return values[0] + "/" + values[1] + "/" + values[2];
    }

    static PositionResult FindPosition(ScenarioResult result, string name){
        for(int i = 0; i < result.Positions.Count; i++) if(result.Positions[i].Name == name) return result.Positions[i];
        return null;
    }

    static double Statistic(Samples samples, string name){
        if(name == "p50") return samples.P50;
        if(name == "p95") return samples.P95;
        if(name == "p99") return samples.P99;
        return samples.Max;
    }

    static string PrivateMb(ScenarioResult result){
        MemorySnapshot memory = result.MemoryAfterTyping != null ? result.MemoryAfterTyping : result.MemoryAfterOpen;
        if(memory == null) return "-";
        return (memory.PrivateBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
    }

    static string WorkingSetMb(MemorySnapshot memory){
        if(memory == null) return "-";
        return (memory.WorkingSetBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
    }

    // The view the live group ran in: the bench's Settings has no HideMarkers seam of its own, so the
    // self-test environment variable (Program.IsSelfTest is true for every bench) is the only switch.
    static bool HideMarkersInEffect(){
        return String.Equals(Environment.GetEnvironmentVariable("MDNOTE_HIDE_MARKERS"), "1", StringComparison.Ordinal);
    }

    static string FormatMs(double value){
        if(value < 0 || Double.IsNaN(value) || Double.IsInfinity(value)) return "-";
        if(value >= 100) return value.ToString("0", CultureInfo.InvariantCulture);
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    static string Format2(double value){
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    static string Shorten(string text, int maximum){
        if(text == null) return "";
        string clean = text.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        if(clean.Length > maximum) clean = clean.Substring(0, maximum) + "...";
        return clean;
    }

    static string Describe(Exception ex){
        if(ex == null) return "";
        string message = ex.GetType().Name + ": " + ex.Message;
        if(ex.InnerException != null) message += " | " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
        if(ex.StackTrace != null) message += " @ " + ex.StackTrace.Replace("\r\n", " <- ");
        if(message.Length > 2000) message = message.Substring(0, 2000) + "...";
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }

    static MemorySnapshot CaptureMemory(bool forceGarbageCollection){
        if(forceGarbageCollection){
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        if(process == null) process = Process.GetCurrentProcess();
        process.Refresh();
        MemorySnapshot memory = new MemorySnapshot();
        memory.PrivateBytes = process.PrivateMemorySize64;
        memory.WorkingSetBytes = process.WorkingSet64;
        memory.ManagedBytes = GC.GetTotalMemory(false);
        return memory;
    }

    static EnvironmentInfo CaptureEnvironment(){
        EnvironmentInfo environment = new EnvironmentInfo();
        environment.ProcessorCount = Environment.ProcessorCount;
        environment.OsVersion = Environment.OSVersion.VersionString;
        environment.Is64Bit = Environment.Is64BitProcess;
        environment.Timestamp = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
        TimeSpan sinceEpoch = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        environment.TimestampUnixMs = (long)sinceEpoch.TotalMilliseconds;
        try{
            System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.PrimaryScreen;
            if(screen != null){
                environment.ScreenWidth = screen.Bounds.Width;
                environment.ScreenHeight = screen.Bounds.Height;
            }
        }catch{ }
        try{
            using(System.Drawing.Graphics graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)){
                environment.DpiX = graphics.DpiX;
                environment.DpiY = graphics.DpiY;
            }
        }catch{ }
        try{
            BenchWindowSize(out environment.BenchWindowWidth, out environment.BenchWindowHeight);
        }catch{ }
        environment.BenchWindowFullscreen = BenchFullscreen();
        try{
            environment.ProcessRenderMode = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString();
        }catch(Exception ex){
            environment.ProcessRenderMode = "unavailable: " + ex.GetType().Name;
        }
        try{
            environment.ExePath = Assembly.GetExecutingAssembly().Location;
            environment.ExeSha256 = ComputeSha256(environment.ExePath);
        }catch(Exception ex){
            environment.ExeSha256 = "unavailable: " + ex.GetType().Name;
        }
        return environment;
    }

    static string ComputeSha256(string path){
        try{
            using(FileStream stream = File.OpenRead(path)){
                using(SHA256 sha = SHA256.Create()){
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder text = new StringBuilder(hash.Length * 2);
                    for(int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    return text.ToString();
                }
            }
        }catch(Exception ex){
            return "unavailable: " + ex.GetType().Name;
        }
    }

    // ------------------------------------------------------------ help types

    sealed class DocData {
        internal string Name;
        internal string Kind;
        internal int NominalChars;
        internal int TargetChars;
        internal int Characters;
        internal string NewlineStyle;
        internal string Description;
        internal bool QuickScaled;
        internal string Text;
    }

    sealed class ScenarioGroup {
        internal string Name;
        internal List<ScenarioResult> Results = new List<ScenarioResult>();
    }

    sealed class ScenarioResult {
        internal string Scenario = "";
        internal string Doc = "";
        internal string Status = "PASS";
        internal string Error = "";
        internal double ElapsedMs = -1;
        internal double OpenMs = -1;
        internal bool OpenRenderSeen;
        internal double JumpEndMs = -1;
        internal bool JumpEndRenderSeen;
        internal double ParseMs = -1;
        internal int RenderTimeouts;
        internal List<PositionResult> Positions = new List<PositionResult>();
        internal Samples CaretMoves = new Samples();
        internal Samples Scroll = new Samples();
        internal double ScrollExtent = -1;
        internal double ScrollViewport = -1;
        internal MemorySnapshot MemoryBeforeOpen;
        internal MemorySnapshot MemoryAfterOpen;
        internal MemorySnapshot MemoryAfterTyping;
        internal long ExpectedLength = -1;
        internal long ActualLength = -1;
        internal bool LengthMatch;
        internal bool FullTextChecked;
        internal bool FullTextMatch;
        internal long FirstDiff = -1;
        internal int MeasuredCharInserts;
        internal int MeasuredEnterInserts;
        internal long EnterCharsInserted;
        // Mermaid scenario only (HasMermaid): the settling wait, the engine counters it saw, and the
        // working set after the pictures were drawn (pairs with memory.before_open).
        internal bool HasMermaid;
        internal bool MermaidSettled;
        internal double MermaidWaitedMs = -1;
        internal double MermaidFirstPictureMs = -1;
        internal int MermaidRendersBefore;
        internal int MermaidStartsBefore;
        internal int MermaidRenders;
        internal int MermaidEngineStarts;
        internal bool MermaidUnavailable;
        internal string MermaidStartupError;
        internal string MermaidLastError;
        internal int MermaidBlockCount;
        internal int MermaidFoldCount;
        internal int MermaidInView;
        internal int MermaidShown;
        // The warm-up pass that draws every diagram before the mermaid scroll samples (WarmUpMermaidScroll):
        // its time, engine renders, the session's block / fold counts at its end, and the in-view blocks
        // that missed their step bound.
        internal double MermaidWarmMs = -1;
        internal int MermaidWarmSteps;
        internal int MermaidWarmPasses;
        internal int MermaidWarmRenders;
        internal int MermaidWarmRendersTotal;
        internal int MermaidWarmBlocks;
        internal int MermaidWarmFolded;
        internal int MermaidWarmUndrawn;
        internal bool MermaidWarmCutShort;
        internal MemorySnapshot MermaidPictureMemory;
    }

    sealed class PositionResult {
        internal string Name = "";
        internal int Line;
        internal int Offset;
        internal Samples CharInsert = new Samples();
        internal Samples EnterInsert = new Samples();
    }

    sealed class Samples {
        internal List<double> Values = new List<double>();
        internal int RenderTimeouts;

        internal void Add(double milliseconds, bool rendered){
            Values.Add(milliseconds);
            if(!rendered) RenderTimeouts++;
        }

        internal int Count { get { return Values.Count; } }
        internal double P50 { get { return Percentile(0.50); } }
        internal double P95 { get { return Percentile(0.95); } }
        internal double P99 { get { return Percentile(0.99); } }

        internal double Max {
            get {
                if(Values.Count == 0) return -1;
                double maximum = Values[0];
                for(int i = 1; i < Values.Count; i++) if(Values[i] > maximum) maximum = Values[i];
                return maximum;
            }
        }

        internal int Over200 {
            get {
                int count = 0;
                for(int i = 0; i < Values.Count; i++) if(Values[i] >= 200.0) count++;
                return count;
            }
        }

        double Percentile(double percentile){
            if(Values.Count == 0) return -1;
            List<double> sorted = new List<double>(Values);
            sorted.Sort();
            double rank = (sorted.Count - 1) * percentile;
            int lower = (int)Math.Floor(rank);
            int upper = (int)Math.Ceiling(rank);
            if(lower == upper) return sorted[lower];
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
        }
    }

    sealed class TimingResult {
        internal double Ms;
        internal bool RenderSeen;
        internal bool TimedOut;
    }

    sealed class MemorySnapshot {
        internal long PrivateBytes;
        internal long WorkingSetBytes;
        internal long ManagedBytes;
    }

    sealed class EnvironmentInfo {
        internal int ProcessorCount;
        internal string OsVersion = "";
        internal bool Is64Bit;
        internal int ScreenWidth;
        internal int ScreenHeight;
        internal double DpiX;
        internal double DpiY;
        internal double BenchWindowWidth;
        internal double BenchWindowHeight;
        internal bool BenchWindowFullscreen;
        internal string ProcessRenderMode = "";
        internal string ExePath = "";
        internal string ExeSha256 = "";
        internal string Timestamp = "";
        internal long TimestampUnixMs;
    }

    sealed class TextTracker {
        internal readonly StringBuilder Text;
        internal long Length;
        internal TextTracker(string source){
            Length = source == null ? 0 : source.Length;
            Text = source != null && source.Length < 2000000 ? new StringBuilder(source) : null;
        }
        internal bool HasText { get { return Text != null; } }
        internal void Insert(int offset, string value){
            if(Text != null){
                if(offset < 0) offset = 0;
                if(offset > Text.Length) offset = Text.Length;
                Text.Insert(offset, value);
            }
            Length += value == null ? 0 : value.Length;
        }
    }

    sealed class Watchdog {
        readonly Stopwatch watch;
        readonly double limitMilliseconds;
        internal Watchdog(double limitMilliseconds){
            this.limitMilliseconds = limitMilliseconds;
            watch = Stopwatch.StartNew();
        }
        internal void Check(string stage){
            if(watch.Elapsed.TotalMilliseconds > limitMilliseconds){
                throw new BenchmarkIncompleteException("watchdog limit " + FormatMs(limitMilliseconds) + " ms exceeded during " + stage);
            }
        }
    }

    sealed class BenchmarkIncompleteException : Exception {
        internal BenchmarkIncompleteException(string message) : base(message){ }
    }

    sealed class JsonOut {
        internal readonly StringBuilder Buffer = new StringBuilder();
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        int indent;
        bool first = true;
        bool afterName;

        void NewItem(){
            if(afterName){
                afterName = false;
                return;
            }
            if(!first) Buffer.Append(',');
            Buffer.Append('\n');
            if(indent > 0) Buffer.Append(' ', indent * 2);
            first = false;
        }

        internal void BeginObject(){ NewItem(); Buffer.Append('{'); indent++; first = true; }
        internal void EndObject(){ indent--; if(!first){ Buffer.Append('\n'); if(indent > 0) Buffer.Append(' ', indent * 2); } Buffer.Append('}'); first = false; }
        internal void BeginArray(){ NewItem(); Buffer.Append('['); indent++; first = true; }
        internal void EndArray(){ indent--; if(!first){ Buffer.Append('\n'); if(indent > 0) Buffer.Append(' ', indent * 2); } Buffer.Append(']'); first = false; }

        internal void Name(string name){
            NewItem();
            Buffer.Append(Quote(name));
            Buffer.Append(": ");
            afterName = true;
        }

        internal void StringValue(string value){
            NewItem();
            if(value == null) Buffer.Append("null");
            else Buffer.Append(Quote(value));
        }

        internal void NumberValue(double value){
            NewItem();
            if(Double.IsNaN(value) || Double.IsInfinity(value)) Buffer.Append("null");
            else Buffer.Append(value.ToString("0.######", Invariant));
        }

        internal void IntegerValue(long value){
            NewItem();
            Buffer.Append(value.ToString(Invariant));
        }

        internal void BoolValue(bool value){
            NewItem();
            Buffer.Append(value ? "true" : "false");
        }

        internal void NullValue(){
            NewItem();
            Buffer.Append("null");
        }

        internal void StringProperty(string name, string value){ Name(name); StringValue(value); }
        internal void NumberProperty(string name, double value){ Name(name); NumberValue(value); }
        internal void IntegerProperty(string name, long value){ Name(name); IntegerValue(value); }
        internal void BoolProperty(string name, bool value){ Name(name); BoolValue(value); }

        internal static string Quote(string value){
            if(value == null) return "null";
            StringBuilder text = new StringBuilder(value.Length + 2);
            text.Append('"');
            for(int i = 0; i < value.Length; i++){
                char c = value[i];
                switch(c){
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    default:
                        if(c < 32){
                            text.Append("\\u");
                            text.Append(((int)c).ToString("x4", Invariant));
                        }else{
                            text.Append(c);
                        }
                        break;
                }
            }
            text.Append('"');
            return text.ToString();
        }
    }
}
}
