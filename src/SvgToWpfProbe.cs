using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MDNote {
// --svg-wpf-probe <folder>: SvgToWpf (Pele SVG -> frozen WPF Drawing). Every *.svg in <folder> is converted twice
// (default light theme, then the dark theme overrides) and rendered to <folder>\png\<name>-light.png and
// -dark.png at 96 dpi on the theme's own background. Synthetic SVGs here cover the edges: element cap, broken
// XML, script and onload. No window, no clipboard, no user file.
static class SvgToWpfProbe {
    static readonly StringBuilder Report = new StringBuilder();
    static bool All = true;
    static int Passes, Fails;
    static string Folder;

    static void Check(string name, bool ok, string detail) {
        if (ok) Passes++; else Fails++;
        All &= ok;
        Report.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (detail != null && detail.Length > 0) Report.Append(": ").Append(detail);
        Report.Append('\n');
    }

    static string N(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }

    internal static int Run(string folder) {
        Folder = Path.GetFullPath(folder);
        Report.Append("SvgToWpfProbe  ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
        Thread thread = new Thread(RunOnSta);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        WriteReport();
        return All ? 0 : 1;
    }

    static void RunOnSta() {
        try {
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(Path.Combine(Folder, "png"));
            SampleChecks();
            TooManyCheck();
            MalformedCheck();
            HostileCheck();
        } catch (Exception ex) {
            Check("probe run", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    static SvgTheme LightTheme() { return new SvgTheme(); }

    static SvgTheme DarkTheme() {
        SvgTheme theme = new SvgTheme();
        theme.Vars["--pele-bg"] = "#1e1e1e";
        theme.Vars["--pele-fg"] = "#e0e0e0";
        theme.Vars["--pele-muted"] = "#a0a0a0";
        theme.Vars["--pele-line"] = "#c8c8c8";
        theme.Vars["--pele-surface"] = "#2b2b2b";
        theme.Vars["--pele-surface-alt"] = "#333333";
        theme.Vars["--pele-border"] = "#6a6a6a";
        theme.Vars["--pele-accent"] = "#7aa2f7";
        return theme;
    }

    static void SampleChecks() {
        string[] files = Directory.GetFiles(Folder, "*.svg");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Check("samples found", files.Length > 0, files.Length + " files");
        long totalMs = 0;
        int converted = 0;
        foreach (string file in files) {
            string name = Path.GetFileNameWithoutExtension(file);
            string svg;
            try {
                svg = File.ReadAllText(file, Encoding.UTF8);
            } catch (Exception ex) {
                Check(name + " read", false, ex.GetType().Name + ": " + ex.Message);
                continue;
            }
            double lightW, lightH, darkW, darkH;
            string error;
            Stopwatch clock = Stopwatch.StartNew();
            Drawing light = SvgToWpf.Convert(svg, LightTheme(), out lightW, out lightH, out error);
            clock.Stop();
            long convertMs = clock.ElapsedMilliseconds;
            totalMs += convertMs;
            int lightElements = SvgToWpf.DiagLastElements;
            int lightSkipped = SvgToWpf.DiagLastSkipped;
            Check(name + " converts", light != null, error == null ? "ok" : error);
            Check(name + " nothing skipped", lightSkipped == 0, "skipped=" + lightSkipped + " elements=" + lightElements);
            if (light == null) continue;
            converted++;
            double declaredW = SvgWidth(svg);
            double declaredH = SvgHeight(svg);
            Check(name + " size " + N(lightW) + "x" + N(lightH), SafeNear(lightW, declaredW) && SafeNear(lightH, declaredH),
                "declared=" + N(declaredW) + "x" + N(declaredH));
            Check(name + " frozen", light.IsFrozen, "frozen=" + light.IsFrozen);
            Drawing dark = SvgToWpf.Convert(svg, DarkTheme(), out darkW, out darkH, out error);
            Check(name + " dark converts", dark != null, error == null ? "ok" : error);
            string lightPng = Path.Combine(Folder, "png", name + "-light.png");
            string darkPng = Path.Combine(Folder, "png", name + "-dark.png");
            Check(name + " light png", Save(light, lightW, lightH, Color.FromRgb(0xff, 0xff, 0xff), lightPng), "png=" + lightPng);
            int expectedW, expectedH;
            RenderPixels(lightW, lightH, out expectedW, out expectedH);
            Check(name + " png size " + expectedW + "x" + expectedH, PngSize(lightPng, expectedW, expectedH), PngActual(lightPng));
            if (dark != null) {
                Check(name + " dark png", Save(dark, darkW, darkH, Color.FromRgb(0x1e, 0x1e, 0x1e), darkPng), "png=" + darkPng);
            }
            Report.Append("INFO ").Append(name).Append(": ").Append(N(lightW)).Append('x').Append(N(lightH))
                .Append(" elements=").Append(lightElements).Append(" skipped=").Append(lightSkipped)
                .Append(" bounds=").Append(Bounds(light))
                .Append(" ms=").Append(convertMs)
                .Append(" png=").Append(expectedW).Append('x').Append(expectedH)
                .Append(RenderScale(lightW, lightH) < 1 ? " (scaled)" : string.Empty).Append('\n');
        }
        Check("all samples converted", converted == files.Length, converted + "/" + files.Length);
        Report.Append("INFO total convert time ").Append(totalMs).Append(" ms for ").Append(files.Length).Append(" files\n");
    }

    static double SvgWidth(string svg) { return RootNumber(svg, "width"); }
    static double SvgHeight(string svg) { return RootNumber(svg, "height"); }

    // The root's width/height, which the probe compares against the converter's own answer. Falls back to the
    // viewBox when the root has no plain numbers there. Only the root tag is searched.
    static double RootNumber(string svg, string name) {
        int head = svg.IndexOf('>');
        string tag = head > 0 ? svg.Substring(0, head) : svg;
        string marker = " " + name + "=\"";
        int at = tag.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0) {
            int start = at + marker.Length;
            int end = tag.IndexOf('"', start);
            if (end > start) {
                double value;
                if (double.TryParse(tag.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            }
        }
        int viewBox = tag.IndexOf("viewBox=\"", StringComparison.Ordinal);
        if (viewBox >= 0) {
            int start = viewBox + 9;
            int end = tag.IndexOf('"', start);
            if (end > start) {
                string[] parts = tag.Substring(start, end - start).Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4) {
                    double value;
                    if (double.TryParse(name == "width" ? parts[2] : parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
                }
            }
        }
        return double.NaN;
    }

    static bool SafeNear(double actual, double declared) {
        if (double.IsNaN(declared)) return true;
        return Math.Abs(actual - declared) <= 1;
    }

    static string Bounds(Drawing drawing) {
        Rect bounds = drawing.Bounds;
        return N(bounds.X) + "," + N(bounds.Y) + " " + N(bounds.Width) + "x" + N(bounds.Height);
    }

    // A big diagram (14621x5512 for big-flow) would need a 320 MB bitmap at 1:1. Past this many pixels the PNG is
    // rendered scaled down; the reported drawing size is unaffected and the scale is noted in the report.
    const double MaxRenderPixels = 4000000;

    static void RenderPixels(double width, double height, out int pixelWidth, out int pixelHeight) {
        double scale = RenderScale(width, height);
        pixelWidth = (int)Math.Max(1, Math.Round(width * scale));
        pixelHeight = (int)Math.Max(1, Math.Round(height * scale));
    }

    static double RenderScale(double width, double height) {
        double pixels = width * height;
        return pixels > MaxRenderPixels ? Math.Sqrt(MaxRenderPixels / pixels) : 1;
    }

    static bool PngSize(string path, int expectedWidth, int expectedHeight) {
        try {
            int actualWidth, actualHeight;
            if (!PngPixels(path, out actualWidth, out actualHeight)) return false;
            return actualWidth == expectedWidth && actualHeight == expectedHeight;
        } catch (Exception) {
            return false;
        }
    }

    static string PngActual(string path) {
        try {
            int width, height;
            if (!PngPixels(path, out width, out height)) return "missing";
            return width + "x" + height;
        } catch (Exception ex) {
            return ex.GetType().Name;
        }
    }

    // Decoded from a stream: a plain "G:\..." path is not a valid Uri.
    static bool PngPixels(string path, out int width, out int height) {
        width = 0;
        height = 0;
        if (!File.Exists(path)) return false;
        using (FileStream stream = File.OpenRead(path)) {
            BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }
        return true;
    }

    static bool Save(Drawing drawing, double width, double height, Color background, string path) {
        try {
            if (drawing == null || !(width > 0) || !(height > 0)) return false;
            double scale = RenderScale(width, height);
            int pixelWidth, pixelHeight;
            RenderPixels(width, height, out pixelWidth, out pixelHeight);
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen()) {
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
                context.DrawDrawing(drawing);
                context.Pop();
            }
            RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
            return true;
        } catch (Exception) {
            return false;
        }
    }

    static void TooManyCheck() {
        StringBuilder svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\" width=\"100\" height=\"100\">");
        for (int i = 0; i < 6000; i++) svg.Append("<rect x=\"1\" y=\"1\" width=\"2\" height=\"2\"/>");
        svg.Append("</svg>");
        double width, height;
        string error;
        Drawing drawing = SvgToWpf.Convert(svg.ToString(), new SvgTheme(), out width, out height, out error);
        Check("6000 rects refused", drawing == null, "null=" + (drawing == null));
        Check("6000 rects says too many elements", error == "too many elements", "error=" + (error == null ? "(none)" : error));
        Check("6000 rects counted", SvgToWpf.DiagLastElements > SvgToWpf.MaxElements, "elements=" + SvgToWpf.DiagLastElements);
        // And one under the cap still converts, so the cap is not swallowing ordinary pictures.
        StringBuilder small = new StringBuilder();
        small.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\" width=\"100\" height=\"100\">");
        for (int i = 0; i < 400; i++) small.Append("<rect x=\"1\" y=\"1\" width=\"2\" height=\"2\"/>");
        small.Append("</svg>");
        Drawing under = SvgToWpf.Convert(small.ToString(), new SvgTheme(), out width, out height, out error);
        Check("400 rects convert", under != null, error == null ? "ok" : error);
    }

    static void MalformedCheck() {
        string[] broken = new string[] {
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect></svg>",
            "<svg",
            "not xml at all",
            "",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect x=\"0\"/></svg>"
        };
        for (int i = 0; i < broken.Length; i++) {
            double width, height;
            string error;
            Drawing drawing = null;
            bool threw = false;
            try {
                drawing = SvgToWpf.Convert(broken[i], new SvgTheme(), out width, out height, out error);
            } catch (Exception) {
                threw = true;
            }
            Check("malformed " + (i + 1) + " no throw", !threw, "threw=" + threw);
            if (i < 4) Check("malformed " + (i + 1) + " null", drawing == null, "null=" + (drawing == null));
        }
    }

    // What a page could sneak into a diagram: <script> as an element and an onload attribute. Both must be
    // counted as skipped and cost nothing else.
    static void HostileCheck() {
        string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 20\" width=\"40\" height=\"20\" onload=\"alert(1)\">" +
            "<script>alert(2)</script>" +
            "<rect x=\"1\" y=\"1\" width=\"10\" height=\"10\" fill=\"#f00\" onload=\"alert(3)\"/>" +
            "<text x=\"2\" y=\"18\" font-size=\"12\">safe</text>" +
            "</svg>";
        double width = 0, height = 0;
        string error = null;
        Drawing drawing = null;
        bool threw = false;
        try {
            drawing = SvgToWpf.Convert(svg, new SvgTheme(), out width, out height, out error);
        } catch (Exception) {
            threw = true;
        }
        Check("script sample no throw", !threw, "threw=" + threw);
        Check("script sample converts", drawing != null, error == null ? "ok" : error);
        Check("script skipped", SvgToWpf.DiagLastSkipped == 1, "skipped=" + SvgToWpf.DiagLastSkipped);
        if (drawing != null) {
            Check("script sample size", SafeNear(width, 40) && SafeNear(height, 20), "size=" + N(width) + "x" + N(height));
            Check("script sample draws its rect and text", drawing.Bounds.Width > 1 && drawing.Bounds.Height > 1, "bounds=" + Bounds(drawing));
            Check("script sample png", Save(drawing, width, height, Colors.White, Path.Combine(Folder, "png", "hostile-light.png")), "png saved");
        }
        // Only an onload attribute, nothing else odd: the picture must convert whole, with no skip at all.
        string attributes =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 30 10\" width=\"30\" height=\"10\" onload=\"alert(4)\">" +
            "<rect x=\"0\" y=\"0\" width=\"30\" height=\"10\" fill=\"#0a0\" onclick=\"alert(5)\"/>" +
            "</svg>";
        Drawing plain = SvgToWpf.Convert(attributes, new SvgTheme(), out width, out height, out error);
        Check("onload only converts", plain != null, error == null ? "ok" : error);
        Check("onload only skips nothing", SvgToWpf.DiagLastSkipped == 0, "skipped=" + SvgToWpf.DiagLastSkipped);
    }

    static void WriteReport() {
        Report.Append('\n').Append("SUMMARY: ").Append(All ? "PASS" : "FAIL")
            .Append(" ").Append(Passes).Append(" PASS, ").Append(Fails).Append(" FAIL\n");
        Report.Append("NOTES\n");
        Report.Append("- Every *.svg in the folder is converted with the default theme and with the dark Pele variable overrides; PNGs go to <folder>\\png\\<name>-light.png and -dark.png.\n");
        Report.Append("- Checks: conversion is non-null, skipped==0, the reported width/height equal the root width/height and the drawing bounds fit inside, the group is frozen, both PNGs are written; then the 5000-element cap, malformed XML and script/onload.\n");
        string text = Report.ToString();
        try { File.WriteAllText(Path.Combine(Folder, "svg-wpf-probe.txt"), text, new UTF8Encoding(false)); } catch (Exception) { }
        string pass = Path.Combine(Folder, "svg-wpf-probe-PASS.txt");
        string fail = Path.Combine(Folder, "svg-wpf-probe-FAIL.txt");
        try { if (File.Exists(All ? fail : pass)) File.Delete(All ? fail : pass); } catch (Exception) { }
        try { File.WriteAllText(All ? pass : fail, text, new UTF8Encoding(false)); } catch (Exception) { }
    }
}
}
