using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
namespace MDNote {
// --mermaid-engine-probe <folder>: Mermaid -> SVG through the chakra.dll engine thread (MermaidEngine.cs).
// Documents are generated here; no user file and no window is touched. Report <folder>\mermaid-engine-probe.txt
// (mermaid-engine-probe-PASS.txt only when every check held, -FAIL.txt otherwise) and each rendered SVG.
// The probe runs on the process's main STA thread and pumps a dispatcher frame while waiting for async work.
// Every wait has a deadline and the whole run is held under Budget milliseconds.
static class MermaidEngineProbe {
    const int Budget = 50000;
    const string Family = "Malgun Gothic";
    static bool all = true;
    static readonly StringBuilder report = new StringBuilder();
    static readonly Stopwatch total = new Stopwatch();

    static void Check(string name, bool ok, string detail) {
        report.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (detail != null) report.Append(": ").Append(detail);
        report.Append('\n');
        if (!ok) all = false;
    }
    static void Info(string text) { report.Append("INFO ").Append(text).Append('\n'); }

    // The longest a single wait may take: 8 s, less when the budget is nearly spent.
    static int Wait() {
        long left = Budget - total.ElapsedMilliseconds;
        if (left < 1) return 1;
        return left > 8000 ? 8000 : (int)left;
    }

    // Runs a dispatcher frame until the condition holds or the deadline passes (MathProbe.Pump style).
    static void PumpUntil(Func<bool> condition, int milliseconds) {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < milliseconds) {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
    }

    // True when the process already has Windows' JsRT engine mapped (a document without mermaid must not).
    static bool ChakraLoaded() {
        try {
            using (Process process = Process.GetCurrentProcess()) {
                foreach (ProcessModule module in process.Modules) {
                    if (string.Equals(module.ModuleName, "chakra.dll", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        } catch (Exception) { }
        return false;
    }

    static MermaidResult Sync(string source) {
        return MermaidEngine.RenderSync(source, Family, 16, 1.0, Wait());
    }
    static bool Good(MermaidResult result) {
        return result != null && result.Error == null && result.Svg != null
            && result.Svg.StartsWith("<svg", StringComparison.Ordinal) && result.Width > 0 && result.Height > 0;
    }
    static string Describe(MermaidResult result) {
        if (result == null) return "null";
        if (result.Error != null) return "error=" + result.Error;
        return result.Width.ToString("0.##", CultureInfo.InvariantCulture) + "x" + result.Height.ToString("0.##", CultureInfo.InvariantCulture)
            + ", " + (result.Svg == null ? 0 : result.Svg.Length) + " chars";
    }

    // ---- generated sources (Korean labels, as in the 2.5.0 spike documents) -----------------

    static string Flowchart() {
        return "flowchart TD\n  A[시작] --> B{조건 확인}\n  B -->|예| C[처리]\n  B -->|아니오| D[종료]\n  C --> D\n";
    }
    static string Sequence() {
        return "sequenceDiagram\n  participant 사용자\n  participant 앱\n  사용자->>앱: 문서 열기\n  앱-->>사용자: 화면 표시\n";
    }
    static string Pie() {
        return "pie title 저장 비율\n  \"문서\" : 60\n  \"그림\" : 30\n  \"기타\" : 10\n";
    }
    static string Gantt() {
        return "gantt\n  title 일정\n  dateFormat YYYY-MM-DD\n  section 개발\n  설계 :a1, 2026-01-01, 5d\n  구현 :a2, after a1, 10d\n";
    }
    static string Mindmap() {
        return "mindmap\n  root((MDNote))\n    편집기\n    미리보기\n    내보내기\n";
    }
    static string ClassDiagram() {
        return "classDiagram\n  class 문서 {\n    +string title\n    +open()\n  }\n  문서 <|-- 노트\n";
    }
    static string StateDiagram() {
        return "stateDiagram-v2\n  [*] --> 편집\n  편집 --> 미리보기\n  미리보기 --> [*]\n";
    }
    // A flowchart with `nodes` labelled nodes, a chain through all of them and `extra` crossing edges.
    // 300 nodes / 600 extra edges stays under the 20,000-character cap and far above a 1 ms limit.
    static string LargeFlowchart(int nodes, int extra) {
        var text = new StringBuilder("flowchart TD\n");
        for (int i = 0; i < nodes; i++) text.Append('N').Append(i).Append("[노드 ").Append(i).Append("]\n");
        for (int i = 0; i + 1 < nodes; i++) text.Append('N').Append(i).Append("-->N").Append(i + 1).Append('\n');
        for (int i = 0; i < extra; i++) {
            int from = i % nodes, to = (i * 7 + 13) % nodes;
            if (from == to) to = (to + 1) % nodes;
            text.Append('N').Append(from).Append("-->N").Append(to).Append('\n');
        }
        return text.ToString();
    }

    internal static int Run(string outDir) {
        string folder = Path.GetFullPath(outDir);
        Directory.CreateDirectory(folder);
        string pass = Path.Combine(folder, "mermaid-engine-probe-PASS.txt"), fail = Path.Combine(folder, "mermaid-engine-probe-FAIL.txt");
        try { File.Delete(pass); } catch (IOException) { }
        try { File.Delete(fail); } catch (IOException) { }
        report.Append("# MermaidEngineProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        total.Start();
        try {
            // 1. Nothing touched the engine before the first request in this process.
            bool loadedBefore = ChakraLoaded();
            Check("1. no request yet: chakra.dll not loaded, no engine start, no engine thread",
                !loadedBefore && MermaidEngine.DiagEngineStarts == 0 && !MermaidEngine.EngineThreadAlive,
                "chakraLoaded=" + loadedBefore + ", starts=" + MermaidEngine.DiagEngineStarts + ", alive=" + MermaidEngine.EngineThreadAlive);

            // 2. Every document type renders; 8. first render (engine start included) and a repeat.
            var sources = new string[] { Flowchart(), Sequence(), Pie(), Gantt(), Mindmap(), ClassDiagram(), StateDiagram() };
            var labels = new string[] { "flowchart", "sequence", "pie", "gantt", "mindmap", "class", "state" };
            for (int i = 0; i < sources.Length; i++) {
                var watch = Stopwatch.StartNew();
                MermaidResult result = Sync(sources[i]);
                watch.Stop();
                bool saved = false;
                if (Good(result)) {
                    try { File.WriteAllText(Path.Combine(folder, labels[i] + ".svg"), result.Svg, new UTF8Encoding(false)); saved = true; } catch (IOException) { }
                }
                Check("2. " + labels[i] + " renders and is saved", Good(result) && saved,
                    Describe(result) + ", " + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms, saved=" + saved);
                if (i == 0) {
                    Info("8. first render (resource inflate + chakra.dll + bundle + flowchart): " + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
                    // The detector of check 1 must be able to say yes, or check 1 proves nothing.
                    bool loadedNow = ChakraLoaded();
                    Check("1b. after the first request: chakra.dll loaded, engine thread alive",
                        MermaidEngine.Unavailable || (loadedNow && MermaidEngine.EngineThreadAlive && MermaidEngine.DiagEngineStarts == 1),
                        "chakraLoaded=" + loadedNow + ", starts=" + MermaidEngine.DiagEngineStarts + ", alive=" + MermaidEngine.EngineThreadAlive + ", unavailable=" + MermaidEngine.Unavailable);
                    watch.Restart();
                    MermaidResult again = Sync(sources[0]);
                    watch.Stop();
                    Info("8. second render (same flowchart): " + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms, " + Describe(again));
                }
            }
            if (MermaidEngine.Unavailable) Info("engine startup error: " + MermaidEngine.StartupError);

            // 3. A broken source reports an error and the engine keeps working afterwards.
            MermaidResult bad = Sync("flowchart TD\n  B[[[");
            MermaidResult next = Sync(Flowchart());
            Check("3. broken source returns an error, next render works",
                bad != null && bad.Error != null && bad.Svg == null && Good(next),
                "bad: " + Describe(bad) + "; next: " + Describe(next));

            // 4. An over-long source is refused before the engine is touched (sync and async).
            int rendersBefore = MermaidEngine.DiagRenders;
            string oversized = "flowchart TD\n" + new string('a', MermaidEngine.MaxSourceChars);
            MermaidResult huge = Sync(oversized);
            MermaidResult hugeAsync = null;
            MermaidEngine.Request("huge", oversized, Family, 16, 1.0, Dispatcher.CurrentDispatcher, delegate(MermaidResult r) { hugeAsync = r; });
            PumpUntil(delegate { return hugeAsync != null; }, Wait());
            Check("4. source over 20 KB refused without a render",
                huge != null && huge.Error == "too large" && hugeAsync != null && hugeAsync.Error == "too large" && MermaidEngine.DiagRenders == rendersBefore,
                "sync: " + Describe(huge) + "; async: " + Describe(hugeAsync) + "; renders " + rendersBefore + " -> " + MermaidEngine.DiagRenders);

            // 5. A 1 ms limit stops a 300-node flowchart, and the engine recovers for the next render.
            string large = LargeFlowchart(300, 600);
            int interruptsBefore = MermaidEngine.DiagInterrupts, startsAtLimit = MermaidEngine.DiagEngineStarts;
            MermaidEngine.TimeLimitMilliseconds = 1;
            var limitWatch = Stopwatch.StartNew();
            MermaidResult stopped = Sync(large);
            limitWatch.Stop();
            MermaidEngine.TimeLimitMilliseconds = 2000;
            int interruptsAfter = MermaidEngine.DiagInterrupts;
            MermaidResult recovered = Sync(Flowchart());
            Check("5. time limit stops the render and the next render works",
                stopped != null && stopped.Error == "time limit" && interruptsAfter > interruptsBefore && Good(recovered),
                "source " + large.Length + " chars; stopped: " + Describe(stopped) + " after " + limitWatch.ElapsedMilliseconds + " ms; interrupts " + interruptsBefore + " -> " + interruptsAfter
                    + "; next: " + Describe(recovered));
            Info("5. runtime " + (MermaidEngine.DiagEngineStarts > startsAtLimit ? "was recreated" : "was re-enabled in place") + " after the limit");

            // 6. Idle timeout: the engine thread disposes the runtime and exits; the next request starts fresh.
            int disposesBefore = MermaidEngine.DiagDisposes, startsBefore = MermaidEngine.DiagEngineStarts;
            MermaidEngine.IdleMilliseconds = 1000;
            var idleWatch = Stopwatch.StartNew();
            PumpUntil(delegate { return !MermaidEngine.EngineThreadAlive && MermaidEngine.DiagDisposes > disposesBefore; }, Wait());
            idleWatch.Stop();
            bool gone = !MermaidEngine.EngineThreadAlive;
            int disposesAfter = MermaidEngine.DiagDisposes;
            MermaidEngine.IdleMilliseconds = 60000;
            MermaidResult restarted = Sync(Flowchart());
            Check("6. idle release, then a new request starts a fresh engine",
                gone && disposesAfter > disposesBefore && Good(restarted) && MermaidEngine.DiagEngineStarts > startsBefore,
                "thread gone=" + gone + " after " + idleWatch.ElapsedMilliseconds + " ms, disposes " + disposesBefore + " -> " + disposesAfter
                    + ", starts " + startsBefore + " -> " + MermaidEngine.DiagEngineStarts + "; next: " + Describe(restarted));

            // 7. Two requests with one key back to back: only the newer callback reports. A much slower
            //    job with another key (the 300-node flowchart of check 5, which may end at the 2 s
            //    limit) goes first so both are still waiting when the second one arrives. The engine
            //    drops the older one in any state, so the result does not depend on that timing.
            int blockerCalls = 0, olderCalls = 0, newerCalls = 0;
            MermaidResult newer = null;
            Dispatcher reply = Dispatcher.CurrentDispatcher;
            MermaidEngine.Request("blocker", large, Family, 16, 1.0, reply, delegate(MermaidResult r) { blockerCalls++; });
            MermaidEngine.Request("same-key", "flowchart TD\n  R1[Older] --> E[End]\n", Family, 16, 1.0, reply, delegate(MermaidResult r) { olderCalls++; });
            MermaidEngine.Request("same-key", "flowchart TD\n  R2[Newer] --> E[End]\n", Family, 16, 1.0, reply, delegate(MermaidResult r) { newerCalls++; newer = r; });
            PumpUntil(delegate { return blockerCalls > 0 && newerCalls > 0; }, Wait());
            // Room for a wrongly delivered older result to show up.
            PumpUntil(delegate { return olderCalls > 0; }, 300);
            bool newest = Good(newer) && newer.Svg.IndexOf("Newer", StringComparison.Ordinal) >= 0;
            Check("7. same key twice: only the newer callback reports",
                blockerCalls == 1 && olderCalls == 0 && newerCalls == 1 && newest,
                "blocker=" + blockerCalls + ", older=" + olderCalls + ", newer=" + newerCalls + ", newer: " + Describe(newer));

            // 7b. The same across the dispatcher hop: the first result is finished and already posted
            //     to this (unpumped) dispatcher when the second request with the key arrives. The
            //     wait for the first render to start sleeps instead of pumping, and the blocking
            //     render behind it returns only after the engine has posted the first result.
            int firstCalls = 0, secondCalls = 0;
            MermaidResult second = null;
            int rendersAtHop = MermaidEngine.DiagRenders;
            MermaidEngine.Request("hop-key", "flowchart TD\n  H1[First] --> E[End]\n", Family, 16, 1.0, reply, delegate(MermaidResult r) { firstCalls++; });
            var hopWatch = Stopwatch.StartNew();
            int hopWait = Wait();
            while (MermaidEngine.DiagRenders == rendersAtHop && hopWatch.ElapsedMilliseconds < hopWait) System.Threading.Thread.Sleep(5);
            bool firstStarted = MermaidEngine.DiagRenders > rendersAtHop;
            MermaidResult barrier = Sync(Flowchart());
            int firstBeforeSecond = firstCalls;
            MermaidEngine.Request("hop-key", "flowchart TD\n  H2[Second] --> E[End]\n", Family, 16, 1.0, reply, delegate(MermaidResult r) { secondCalls++; second = r; });
            PumpUntil(delegate { return secondCalls > 0; }, Wait());
            PumpUntil(delegate { return firstCalls > 0; }, 300);
            bool secondOk = Good(second) && second.Svg.IndexOf("Second", StringComparison.Ordinal) >= 0;
            Check("7b. same key again after the first result was posted: only the second callback reports",
                firstStarted && Good(barrier) && firstBeforeSecond == 0 && firstCalls == 0 && secondCalls == 1 && secondOk,
                "first render started=" + firstStarted + ", barrier: " + Describe(barrier) + ", first before pump=" + firstBeforeSecond
                    + ", first=" + firstCalls + ", second=" + secondCalls + ", second: " + Describe(second));

            Check("budget: probe finished inside " + Budget + " ms", total.ElapsedMilliseconds < Budget, total.ElapsedMilliseconds + " ms");
        } catch (Exception ex) {
            all = false;
            report.Append("FAIL probe exception: ").Append(ex).Append('\n');
        } finally {
            MermaidEngine.TimeLimitMilliseconds = 2000;
            MermaidEngine.IdleMilliseconds = 60000;
        }
        report.Append("\n- DiagEngineStarts=").Append(MermaidEngine.DiagEngineStarts)
            .Append(", DiagRenders=").Append(MermaidEngine.DiagRenders)
            .Append(", DiagDisposes=").Append(MermaidEngine.DiagDisposes)
            .Append(", DiagInterrupts=").Append(MermaidEngine.DiagInterrupts)
            .Append(", Unavailable=").Append(MermaidEngine.Unavailable)
            .Append(", total ").Append(total.ElapsedMilliseconds).Append(" ms\n");
        report.Append("\n").Append(all ? "RESULT PASS" : "RESULT FAIL").Append('\n');
        File.WriteAllText(Path.Combine(folder, "mermaid-engine-probe.txt"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllText(all ? pass : fail, report.ToString(), new UTF8Encoding(false));
        return all ? 0 : 1;
    }
}
}
