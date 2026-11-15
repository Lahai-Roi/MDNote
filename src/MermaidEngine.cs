using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
namespace MDNote {
// 2.5.0: Mermaid -> SVG through Pele (src\lib\pele\pele.chakra.min.js, embedded deflated) running in
// Windows' own chakra.dll (JsRT). JsRT allows a runtime only on the thread that made it current, so one
// STA background thread owns the runtime, the context, the bundle and the text measurer; callers only
// queue work. Nothing here loads chakra.dll until the first real request: every P/Invoke sits in Native
// and is reached only through NoInlining helpers (the same boundary MathEngine uses for WpfMath).
// Any start failure (no chakra.dll, bad image, missing resource, JsRT error) sets Unavailable for the
// rest of the process: requests then return Error "unavailable" at once and nothing is retried.
internal sealed class MermaidResult {
    internal string Svg;
    internal double Width;
    internal double Height;
    internal string Error;
}
static class MermaidEngine {
    internal static volatile bool Unavailable;
    // The failure that set Unavailable (type, JsRT code and script message), for the probe report.
    internal static string StartupError;
    internal static int IdleMilliseconds = 60000;
    internal static int TimeLimitMilliseconds = 2000;
    internal const int MaxSourceChars = 20000;
    internal static int DiagEngineStarts, DiagRenders, DiagDisposes, DiagInterrupts;

    static readonly object gate = new object();
    // Async requests wait in this list; RenderSync requests are added at the front. A job with an
    // equal key that is still waiting is dropped from here (its done is never called).
    static readonly LinkedList<Job> waiting = new LinkedList<Job>();
    // The job the engine thread is rendering right now. A newer equal-key request supersedes it too,
    // so a burst of requests for one block ends with exactly one done call (for the newest).
    static Job running;
    // The newest async job per key, from Request until its done has run (or it was dropped). A newer
    // request marks the entry Superseded whatever state it is in - waiting, rendering, or finished
    // and already posted to the caller's dispatcher - so that older done never runs.
    static readonly Dictionary<object, Job> latest = new Dictionary<object, Job>();
    // An access violation came out of chakra.dll. Unavailable is set with it, and from then on
    // nothing calls into chakra again, not even to dispose the runtime. The process may still be
    // unstable after such a fault; this only avoids an immediate crash.
    static volatile bool fatal;
    static Thread thread;
    // The family the current render measures with (engine thread only; set before JsRunScript).
    static string hostFamily = "Segoe UI";
    static double hostPixelsPerDip = 1.0;
    // The runtime the current render runs on (engine thread only), for the measurer's disabled check.
    static IntPtr hostRuntime;

    internal static bool EngineThreadAlive { get { lock (gate) { return thread != null && thread.IsAlive; } } }

    sealed class Job {
        internal object Key;
        internal string Source, Family;
        internal double Size, PixelsPerDip;
        internal Dispatcher Reply;
        internal Action<MermaidResult> Done;
        // RenderSync only: the blocking caller waits on gate (Monitor.Wait) until Completed, then reads Result.
        internal bool Sync;
        // Set by Finish under gate, together with Result.
        internal bool Completed;
        internal MermaidResult Result;
        // RenderSync gave up waiting; the engine skips the job instead of rendering it.
        internal bool Abandoned;
        // A newer request with an equal key arrived; this result is dropped (done never called).
        // Read and written under gate only.
        internal bool Superseded;
    }

    // ---- caller side ---------------------------------------------------------------------

    // Non-blocking. At most one waiting request per key: a newer request with an equal key replaces
    // the older one, so typing in a mermaid block only ever renders its newest text.
    // done runs on reply's thread (Background priority); with reply == null it runs on the engine thread.
    // Errors: "unavailable" (no usable chakra.dll in this process), "too large", "time limit", or Pele's message.
    internal static void Request(object key, string source, string fontFamily, double fontSize, double pixelsPerDip, Dispatcher reply, Action<MermaidResult> done) {
        Job job = new Job();
        job.Key = key;
        job.Source = source;
        job.Family = fontFamily;
        job.Size = fontSize;
        job.PixelsPerDip = pixelsPerDip;
        job.Reply = reply;
        job.Done = done;
        // A refused request is still the newest one for its key: it supersedes the older ones and
        // can itself be superseded before its error is delivered.
        string refused = source != null && source.Length > MaxSourceChars ? "too large" : null;
        lock (gate) {
            if (key != null) { ReplaceLocked(key); latest[key] = job; }
            if (refused == null && Unavailable) refused = "unavailable";
            if (refused == null) {
                waiting.AddLast(job);
                EnsureEngineLocked();
                Monitor.PulseAll(gate);
            }
        }
        if (refused != null) Finish(job, Fail(refused));
    }

    // Drops every still-waiting async request with this key and supersedes the previous newest one,
    // whether it is waiting, being rendered, or finished with its done still queued on a dispatcher.
    // A superseded request's done is never called.
    static void ReplaceLocked(object key) {
        LinkedListNode<Job> node = waiting.First;
        while (node != null) {
            LinkedListNode<Job> next = node.Next;
            if (!node.Value.Sync && object.Equals(node.Value.Key, key)) { node.Value.Superseded = true; waiting.Remove(node); }
            node = next;
        }
        if (running != null && !running.Sync && object.Equals(running.Key, key)) running.Superseded = true;
        Job previous;
        if (latest.TryGetValue(key, out previous)) previous.Superseded = true;
    }
    // Takes the job out of the per-key map if it is still the newest for its key. Caller holds gate.
    static void ForgetLocked(Job job) {
        Job current;
        if (job.Key != null && latest.TryGetValue(job.Key, out current) && current == job) latest.Remove(job.Key);
    }

    // Blocks the calling thread. Runs the render at the front of the queue; the engine thread is never
    // the caller. Gives up after waitMilliseconds ("timeout"); a render that finishes later is dropped.
    internal static MermaidResult RenderSync(string source, string fontFamily, double fontSize, double pixelsPerDip, int waitMilliseconds) {
        if (IsEngineThread()) return Fail("reentrant");
        if (Unavailable) return Fail("unavailable");
        if (source != null && source.Length > MaxSourceChars) return Fail("too large");
        Job job = new Job();
        job.Key = null;
        job.Source = source;
        job.Family = fontFamily;
        job.Size = fontSize;
        job.PixelsPerDip = pixelsPerDip;
        job.Sync = true;
        int wait = waitMilliseconds < 1 ? 1 : waitMilliseconds;
        lock (gate) {
            if (Unavailable) return Fail("unavailable");
            waiting.AddFirst(job);
            EnsureEngineLocked();
            Monitor.PulseAll(gate);
            // No event handle: wait on gate itself. It is pulsed for other reasons too (new work,
            // other jobs finishing), so the flag and the deadline are rechecked after every wake.
            int start = Environment.TickCount;
            while (!job.Completed) {
                int remaining = wait - unchecked(Environment.TickCount - start);
                if (remaining <= 0) break;
                Monitor.Wait(gate, remaining);
            }
            if (job.Completed && job.Result != null) return job.Result;
            // The engine skips an abandoned job; one already rendering finishes into a job nobody reads.
            job.Abandoned = true;
            return Fail("timeout");
        }
    }

    static bool IsEngineThread() {
        lock (gate) return thread != null && Thread.CurrentThread == thread;
    }

    static MermaidResult Fail(string message) {
        MermaidResult result = new MermaidResult();
        result.Error = message;
        return result;
    }

    // The last step of every async delivery, on the thread done runs on: a job superseded at any
    // point up to here (also after it was posted to the dispatcher) is dropped. Never called with gate held.
    static void Deliver(Job job, MermaidResult result) {
        bool deliver;
        lock (gate) { deliver = !job.Superseded; ForgetLocked(job); }
        if (deliver) SafeDone(job.Done, result);
    }

    static void SafeDone(Action<MermaidResult> done, MermaidResult result) {
        try { done(result); } catch (Exception) { }
    }

    // Completes a job: wakes a RenderSync caller, or sends done through the caller's dispatcher (a
    // shut-down dispatcher drops it), or runs it right here when the caller gave none.
    static void Finish(Job job, MermaidResult result) {
        lock (gate) {
            job.Result = result;
            job.Completed = true;
            Monitor.PulseAll(gate);
            if (job.Sync || job.Done == null) { ForgetLocked(job); return; }
        }
        Dispatcher reply = job.Reply;
        if (reply == null) { Deliver(job, result); return; }
        bool posted = false;
        try {
            if (!reply.HasShutdownStarted) {
                reply.BeginInvoke(DispatcherPriority.Background, new Action(delegate { Deliver(job, result); }));
                posted = true;
            }
        } catch (Exception) { }
        // Nothing will run the delegate, so the map entry is released here instead.
        if (!posted) { lock (gate) ForgetLocked(job); }
    }

    // Corrupted-state faults. Called from the catch blocks of the methods that call into chakra.
    static void NoteFault(Exception ex) {
        if (!(ex is AccessViolationException)) return;
        if (!fatal) StartupError = ex.GetType().Name + ": " + ex.Message;
        Unavailable = true;
        fatal = true;
    }

    // ---- engine thread -------------------------------------------------------------------

    // One engine at most: caller holds gate. The field is cleared only after a released engine has
    // disposed its runtime, so a fresh thread never starts on top of a live runtime.
    static void EnsureEngineLocked() {
        if (Unavailable) return;
        if (thread != null && thread.IsAlive) return;
        Thread created = new Thread(Run);
        created.SetApartmentState(ApartmentState.STA);
        created.IsBackground = true;
        created.Name = "MDNote Mermaid";
        thread = created;
        created.Start();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static Engine StartEngine() {
        if (fatal) return null;
        Engine engine = new Engine();
        string error;
        bool ok;
        // The call itself is what binds chakra.dll, so a missing/incompatible DLL surfaces here as a
        // catchable exception; Engine.Start's own catch cannot see a JIT failure of its own method.
        try { ok = engine.Start(out error); }
        catch (Exception ex) {
            NoteFault(ex);
            ok = false;
            error = ex.GetType().Name + ": " + ex.Message;
        }
        if (ok && !fatal) {
            lock (gate) DiagEngineStarts++;
            return engine;
        }
        lock (gate) { Unavailable = true; if (error != null) StartupError = error; }
        // Dispose makes no chakra call once a fatal fault was noted.
        try { engine.Dispose(); } catch (Exception ex) { NoteFault(ex); }
        return null;
    }

    // .NET 4.8 does not hand an AccessViolationException from native code to an ordinary catch, so
    // the engine-thread methods that call chakra carry HandleProcessCorruptedStateExceptions. After
    // such a fault: Unavailable for the rest of the process, every waiting job fails, the runtime is
    // left alone (no JsDisposeRuntime) and this thread exits. The process may still be unstable
    // after an access violation; this only avoids an immediate crash.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static void Run() {
        Engine engine = null;
        try {
            engine = StartEngine();
            if (engine == null) { FailWaiting("unavailable"); return; }
            Loop(ref engine);
        } catch (Exception ex) {
            // A fault JsRT cannot be trusted after: stop retrying and answer everything waiters hold.
            NoteFault(ex);
            lock (gate) { Unavailable = true; StartupError = ex.GetType().Name + ": " + ex.Message; }
            try { if (engine != null) engine.Dispose(); } catch (Exception inner) { NoteFault(inner); }
            Job current;
            lock (gate) { current = running; running = null; }
            if (current != null) Finish(current, Fail("unavailable"));
            FailWaiting("unavailable");
        } finally {
            // FormattedText made a Dispatcher on this thread; shut it down on every way out.
            try {
                Dispatcher dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher != null) dispatcher.InvokeShutdown();
            } catch (Exception) { }
        }
    }
    static void Loop(ref Engine engine) {
        int renders = 0;
        // Idle time is counted from the moment the queue emptied, and the wait is sliced so a change
        // to IdleMilliseconds (the probe, settings) is seen within half a second.
        int lastWork = Environment.TickCount;
        while (true) {
            Job job = null;
            bool exit = false;
            lock (gate) {
                while (true) {
                    if (waiting.Count > 0) {
                        job = waiting.First.Value;
                        waiting.RemoveFirst();
                        running = job;
                        break;
                    }
                    int idle = IdleMilliseconds;
                    if (idle < 1) idle = 1;
                    int elapsed = unchecked(Environment.TickCount - lastWork);
                    int remaining = idle - elapsed;
                    if (remaining <= 0) { exit = true; break; }
                    Monitor.Wait(gate, remaining > 500 ? 500 : remaining);
                }
            }
            if (exit) {
                // Idle with an empty queue: dispose here, on the owning thread, then let the thread
                // exit. The thread field stays set while disposing, so no second engine can start on
                // a live runtime; work that arrived meanwhile starts a fresh thread instead.
                engine.Dispose();
                lock (gate) {
                    thread = null;
                    if (waiting.Count > 0) EnsureEngineLocked();
                }
                return;
            }
            bool abandoned, superseded;
            lock (gate) { abandoned = job.Abandoned; superseded = job.Superseded; }
            if (abandoned || superseded) {
                // A blocking caller that gave up, or a newer request with the same key: the render
                // is skipped, so done is never called and no caller is woken.
                lock (gate) { running = null; ForgetLocked(job); }
                lastWork = Environment.TickCount;
                continue;
            }
            MermaidResult result = engine.Render(job.Source, job.Family, job.Size, job.PixelsPerDip);
            renders++;
            // A job superseded during the render is dropped here; one superseded later, while its
            // done waits on the caller's dispatcher, is dropped by Deliver.
            bool deliver;
            lock (gate) { deliver = !job.Superseded; running = null; if (!deliver) ForgetLocked(job); }
            if (deliver) Finish(job, result);
            if (fatal) {
                // chakra faulted: no restart, and Dispose only forgets the handles. Answer whoever
                // waits and let the thread end.
                engine.Dispose();
                FailWaiting("unavailable");
                return;
            }
            if (engine.Broken) {
                // The time limit could not be undone (or the call faulted): replace the runtime
                // before anything else runs on it.
                engine.Dispose();
                engine = StartEngine();
                if (engine == null) { FailWaiting("unavailable"); return; }
                renders = 0;
            }
            bool empty;
            lock (gate) empty = waiting.Count == 0;
            if (empty || renders % 10 == 0) engine.Garbage();
            lastWork = Environment.TickCount;
        }
    }

    // Fails every waiting job (start failure). Sync callers get Result, async ones their callback.
    static void FailWaiting(string message) {
        var failed = new List<Job>();
        lock (gate) {
            while (waiting.Count > 0) {
                failed.Add(waiting.First.Value);
                waiting.RemoveFirst();
            }
            thread = null;
        }
        foreach (Job job in failed) Finish(job, Fail(message));
    }

    // ---- text measurement (engine thread) ------------------------------------------------

    // WPF widths in DIPs for Pele's measurer. Style bits: 1 bold, 2 italic, 4 mono, 8 sub, 16 sup,
    // 32 underline. Pele passes the full size with sub/sup and expects the measurer to shrink it
    // (its own measurers divide by 1.2). A font family is taken from the request; mono always
    // measures with Consolas. Any failure returns 0 so no .NET exception ever crosses into chakra.
    internal static double MeasureText(string text, double size, int flags) {
        try {
            if (text == null) text = "";
            if (double.IsNaN(size) || size < 1) size = 1;
            if (size > 400) size = 400;
            if ((flags & 24) != 0) size = size / 1.2;
            double pixelsPerDip = hostPixelsPerDip;
            string family = (flags & 4) != 0 ? "Consolas" : hostFamily;
            string key = family + "|" + (flags & 7).ToString(CultureInfo.InvariantCulture) + "|" + size.ToString("0.###", CultureInfo.InvariantCulture)
                + "|" + pixelsPerDip.ToString("0.###", CultureInfo.InvariantCulture) + "|" + text;
            double width;
            if (widths.TryGetValue(key, out width)) return width;
            string faceKey = family + "|" + (flags & 3).ToString(CultureInfo.InvariantCulture);
            Typeface face;
            if (!faces.TryGetValue(faceKey, out face)) {
                face = new Typeface(new FontFamily(family), (flags & 2) != 0 ? FontStyles.Italic : FontStyles.Normal, (flags & 1) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                if (faces.Count >= 64) faces.Clear();
                faces[faceKey] = face;
            }
            FormattedText formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, pixelsPerDip);
            width = formatted.WidthIncludingTrailingWhitespace;
            if (widths.Count >= 20000) widths.Clear();
            widths[key] = width;
            return width;
        } catch (Exception) {
            return 0;
        }
    }
    static readonly Dictionary<string, Typeface> faces = new Dictionary<string, Typeface>(StringComparer.Ordinal);
    static readonly Dictionary<string, double> widths = new Dictionary<string, double>(StringComparer.Ordinal);

    // ---- one runtime lifetime ------------------------------------------------------------

    // The JS call PeleSpike.Call proved: a function wrapper turns a Pele throw (bad syntax, Pele's own
    // limits) into a plain "ERR|..." string. A time-limit termination is not catchable in script; it
    // comes back as a JsRunScript error code instead.
    static string BuildCall(string source, string family, double size) {
        return "(function(){try{var r=pele.render(" + JsString(source) + ",{fontFamily:" + JsString(string.IsNullOrEmpty(family) ? "Segoe UI" : family)
            + ",fontSize:" + size.ToString("0.###", CultureInfo.InvariantCulture)
            + ",links:false,measurer:{width:function(t,s,f){return hostMeasure(t,s,f);}}});"
            + "return 'OK|'+r.width+'|'+r.height+'|'+r.svg;}catch(e){return 'ERR|'+(e&&e.message?e.message:String(e));}})()";
    }
    static string JsString(string s) {
        if (s == null) s = "";
        StringBuilder b = new StringBuilder("\"");
        foreach (char c in s) {
            if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
            else if (c < 32 || c > 126) { b.Append("\\u").Append(((int)c).ToString("x4")); }
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }

    // Runtime + context + bundle for one engine start. Every chakra call goes through Native; the
    // class itself is reached only from an engine thread (Start from the NoInlining StartEngine,
    // the rest from Run), so a document without mermaid never binds chakra.dll.
    sealed class Engine {
        IntPtr runtime, context;
        // The measurer delegate chakra holds a raw pointer to. Static and never cleared, so it
        // outlives every runtime (a collected delegate would crash the next width call).
        static Native.NativeFn keep;
        int cookie;
        readonly object timerLock = new object();
        Timer timer;
        int generation;
        bool disposed;
        // True when the time limit left the runtime disabled and re-enabling failed.
        internal bool Broken;

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        internal bool Start(out string error) {
            error = null;
            IntPtr rt = IntPtr.Zero, ctx = IntPtr.Zero;
            try {
                int code = Native.JsCreateRuntime(Native.AllowScriptInterrupt | Native.DisableEval, IntPtr.Zero, out rt);
                if (code != 0) { error = "runtime 0x" + code.ToString("x"); return false; }
                // Kept from here on so Dispose releases it whichever later step fails.
                runtime = rt;
                code = Native.JsCreateContext(rt, out ctx);
                if (code != 0) { error = "context 0x" + code.ToString("x"); return false; }
                code = Native.JsSetCurrentContext(ctx);
                if (code != 0) { error = "current context 0x" + code.ToString("x"); return false; }
                context = ctx;
                IntPtr glob = IntPtr.Zero, id = IntPtr.Zero, function;
                if (keep == null) keep = Native.MeasureFn;
                code = Native.JsCreateFunction(keep, IntPtr.Zero, out function);
                if (code == 0) code = Native.JsGetGlobalObject(out glob);
                if (code == 0) code = Native.JsGetPropertyIdFromName("hostMeasure", out id);
                if (code == 0) code = Native.JsSetProperty(glob, id, function, true);
                if (code != 0) { error = "measurer 0x" + code.ToString("x") + ExceptionText(); return false; }
                string bundle = Bundle();
                if (bundle == null) { error = "resource pele.js.deflate is missing"; return false; }
                IntPtr result;
                code = Native.JsRunScript(bundle, new IntPtr(++cookie), "pele.js", out result);
                if (code != 0) { error = "bundle 0x" + code.ToString("x") + ExceptionText(); return false; }
                return true;
            } catch (DllNotFoundException ex) { error = "chakra.dll: " + ex.Message; }
            catch (EntryPointNotFoundException ex) { error = "chakra.dll entry point: " + ex.Message; }
            catch (BadImageFormatException ex) { error = "chakra.dll image: " + ex.Message; }
            catch (SEHException ex) { error = "chakra.dll fault: " + ex.Message; }
            catch (Exception ex) { NoteFault(ex); error = ex.GetType().Name + ": " + ex.Message; }
            return false;
        }
        // UTF-8 Pele bundle from the exe's own resource; inflated fresh per engine start (about 14 ms,
        // and releasing it is what brings memory back down after the idle shutdown).
        static string Bundle() {
            using (Stream raw = Assembly.GetExecutingAssembly().GetManifestResourceStream("pele.js.deflate")) {
                if (raw == null) return null;
                using (DeflateStream deflate = new DeflateStream(raw, CompressionMode.Decompress))
                using (StreamReader reader = new StreamReader(deflate, new UTF8Encoding(false)))
                    return reader.ReadToEnd();
            }
        }
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        static string ExceptionText() {
            if (fatal) return "";
            try {
                IntPtr exception;
                if (Native.JsGetAndClearException(out exception) != 0 || exception == IntPtr.Zero) return "";
                return ": " + Native.Str(exception);
            } catch (Exception ex) { NoteFault(ex); return ""; }
        }

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        internal MermaidResult Render(string source, string family, double size, double pixelsPerDip) {
            MermaidResult result = new MermaidResult();
            if (fatal || runtime == IntPtr.Zero || context == IntPtr.Zero) { result.Error = "unavailable"; return result; }
            hostRuntime = runtime;
            hostFamily = string.IsNullOrEmpty(family) ? "Segoe UI" : family;
            hostPixelsPerDip = double.IsNaN(pixelsPerDip) || pixelsPerDip < 0.25 || pixelsPerDip > 16 ? 1.0 : pixelsPerDip;
            if (double.IsNaN(size) || size < 4) size = 4;
            if (size > 128) size = 128;
            DiagRenders++;
            string call = BuildCall(source, hostFamily, size);
            Arm();
            IntPtr value = IntPtr.Zero;
            int code;
            try { code = Native.JsRunScript(call, new IntPtr(++cookie), "", out value); }
            catch (Exception ex) {
                NoteFault(ex);
                Disarm();
                // A structured-exception or similar fault leaves the runtime unusable; recreate it
                // (after an access violation the caller leaves it alone instead and stops for good).
                Broken = true;
                result.Error = fatal ? "unavailable" : ex.GetType().Name + ": " + ex.Message;
                return result;
            }
            Disarm();
            // The measurer or the time-limit timer saw an access violation during the script.
            if (fatal) { Broken = true; result.Error = "unavailable"; return result; }
            if (code == 0) {
                if (ExecutionDisabled()) Recover();
                string payload = "";
                try { if (!fatal) payload = Native.Str(value); } catch (Exception ex) { NoteFault(ex); }
                if (fatal) { Broken = true; result.Error = "unavailable"; return result; }
                if (payload.StartsWith("OK|", StringComparison.Ordinal)) {
                    string[] parts = payload.Split(new char[] { '|' }, 4);
                    double number;
                    if (parts.Length == 4 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out number)) result.Width = number;
                    if (parts.Length == 4 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out number)) result.Height = number;
                    if (parts.Length == 4) result.Svg = parts[3];
                    if (result.Svg == null || result.Svg.Length == 0) result.Error = "empty svg";
                } else if (payload.StartsWith("ERR|", StringComparison.Ordinal)) {
                    result.Error = payload.Substring(4);
                } else {
                    result.Error = payload.Length == 0 ? "no result" : payload;
                }
                return result;
            }
            // A stopped render: the timer disabled the runtime (script terminated) or it was
            // already disabled when the call started. Turn execution back on; if the runtime does
            // not come back, Broken makes the caller replace it with a fresh one. The disabled-state
            // query is the real test (Disarm has already waited out any timer callback); the codes
            // only cover a query that fails.
            if (ExecutionDisabled() || code == Native.ErrorScriptTerminated || code == Native.ErrorInDisabledState) {
                Recover();
                result.Error = "time limit";
                return result;
            }
            string text = ExceptionText();
            result.Error = text.Length > 2 ? text.Substring(2) : "script 0x" + code.ToString("x");
            return result;
        }

        // ---- time limit (timer runs on a pool thread; only JsDisableRuntimeExecution is allowed off the owning thread) ----
        // A fresh timer per render, each with its own token. A callback that is late (queued before
        // Disarm, run after) sees a newer generation and does nothing, so it can never stop the next
        // render or touch a disposed runtime.
        void Arm() {
            lock (timerLock) {
                generation++;
                if (disposed) return;
                int token = generation;
                int limit = TimeLimitMilliseconds < 1 ? 1 : TimeLimitMilliseconds;
                if (timer != null) timer.Dispose();
                timer = new Timer(delegate(object state) { OnTimeLimit(token); }, null, limit, Timeout.Infinite);
            }
        }
        void Disarm() {
            lock (timerLock) {
                generation++;
                if (timer != null) { timer.Dispose(); timer = null; }
            }
        }
        // Pool thread: an access violation here would otherwise end the process from a thread nobody guards.
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        void OnTimeLimit(int token) {
            lock (timerLock) {
                if (fatal || disposed || token != generation || runtime == IntPtr.Zero) return;
                try { DiagInterrupts++; Native.JsDisableRuntimeExecution(runtime); } catch (Exception ex) { NoteFault(ex); }
            }
        }
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        bool ExecutionDisabled() {
            if (fatal) return false;
            try {
                bool disabled;
                if (Native.JsIsRuntimeExecutionDisabled(runtime, out disabled) != 0) return true;
                return disabled;
            } catch (Exception ex) { NoteFault(ex); return false; }
        }
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        void Recover() {
            if (fatal) { Broken = true; return; }
            try {
                if (Native.JsEnableRuntimeExecution(runtime) != 0) { Broken = true; return; }
                bool disabled;
                if (Native.JsIsRuntimeExecutionDisabled(runtime, out disabled) != 0 || disabled) { Broken = true; return; }
                IntPtr exception;
                Native.JsGetAndClearException(out exception);
            } catch (Exception ex) { NoteFault(ex); Broken = true; }
        }
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        internal void Garbage() {
            if (fatal) return;
            try { Native.JsCollectGarbage(runtime); } catch (Exception ex) { NoteFault(ex); }
        }

        // After a fatal fault this only stops the timer and forgets the handles: the runtime is
        // never touched again (no JsSetCurrentContext, no JsDisposeRuntime) and is left to the process.
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        internal void Dispose() {
            IntPtr rt, ctx;
            lock (timerLock) {
                disposed = true;
                generation++;
                if (timer != null) { timer.Dispose(); timer = null; }
                rt = runtime;
                ctx = context;
                runtime = IntPtr.Zero;
                context = IntPtr.Zero;
            }
            if (rt != IntPtr.Zero && hostRuntime == rt) hostRuntime = IntPtr.Zero;
            if (fatal) return;
            if (ctx != IntPtr.Zero) {
                try { Native.JsSetCurrentContext(IntPtr.Zero); } catch (Exception ex) { NoteFault(ex); }
            }
            if (rt != IntPtr.Zero && !fatal) {
                try { Native.JsDisposeRuntime(rt); } catch (Exception ex) { NoteFault(ex); }
                lock (gate) DiagDisposes++;
            }
        }
    }

    // ---- chakra.dll (JsRT). Nothing outside this class names a P/Invoke. -------------------
    static class Native {
        internal const int AllowScriptInterrupt = 0x2;
        internal const int DisableEval = 0x10;
        internal const int ErrorInDisabledState = 0x10009;
        internal const int ErrorScriptException = 0x30001;
        internal const int ErrorScriptTerminated = 0x30003;

        [DllImport("chakra.dll")] internal static extern int JsCreateRuntime(int attributes, IntPtr threadService, out IntPtr runtime);
        [DllImport("chakra.dll")] internal static extern int JsCreateContext(IntPtr runtime, out IntPtr context);
        [DllImport("chakra.dll")] internal static extern int JsSetCurrentContext(IntPtr context);
        [DllImport("chakra.dll")] internal static extern int JsDisposeRuntime(IntPtr runtime);
        [DllImport("chakra.dll", CharSet = CharSet.Unicode)] internal static extern int JsRunScript(string script, IntPtr sourceContext, string sourceUrl, out IntPtr result);
        [DllImport("chakra.dll")] internal static extern int JsConvertValueToString(IntPtr value, out IntPtr str);
        [DllImport("chakra.dll")] internal static extern int JsStringToPointer(IntPtr value, out IntPtr chars, out UIntPtr length);
        [DllImport("chakra.dll")] internal static extern int JsGetAndClearException(out IntPtr exception);
        [DllImport("chakra.dll")] internal static extern int JsGetGlobalObject(out IntPtr global);
        [DllImport("chakra.dll", CharSet = CharSet.Unicode)] internal static extern int JsGetPropertyIdFromName(string name, out IntPtr id);
        [DllImport("chakra.dll")] internal static extern int JsSetProperty(IntPtr obj, IntPtr id, IntPtr value, bool strict);
        [DllImport("chakra.dll")] internal static extern int JsCreateFunction(NativeFn fn, IntPtr state, out IntPtr func);
        [DllImport("chakra.dll")] internal static extern int JsNumberToDouble(IntPtr value, out double d);
        [DllImport("chakra.dll")] internal static extern int JsDoubleToNumber(double d, out IntPtr value);
        [DllImport("chakra.dll")] internal static extern int JsGetUndefinedValue(out IntPtr undefinedValue);
        [DllImport("chakra.dll")] internal static extern int JsDisableRuntimeExecution(IntPtr runtime);
        [DllImport("chakra.dll")] internal static extern int JsEnableRuntimeExecution(IntPtr runtime);
        [DllImport("chakra.dll")] internal static extern int JsCollectGarbage(IntPtr runtime);
        // Chakra writes a C++ bool (one byte) here, so it must not be marshalled as a Win32 BOOL.
        [DllImport("chakra.dll")] internal static extern int JsIsRuntimeExecutionDisabled(IntPtr runtime, [MarshalAs(UnmanagedType.I1)] out bool disabled);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate IntPtr NativeFn(IntPtr callee, bool isConstruct, IntPtr args, ushort argCount, IntPtr state);

        // Pele's measurer hook. Argument 0 is `this`; the width callback receives (text, size, style).
        // A .NET exception must never cross back into chakra, and neither may a null reference
        // (JS_INVALID_REFERENCE): when the time limit disabled the runtime mid-callback every
        // allocating call fails, so the answer falls back number -> undefined -> `this` -> callee.
        // The last two are references chakra itself passed in and need no call at all.
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        internal static IntPtr MeasureFn(IntPtr callee, bool isConstruct, IntPtr args, ushort argCount, IntPtr state) {
            IntPtr answer = IntPtr.Zero, fallback = callee;
            try {
                if (argCount >= 1 && args != IntPtr.Zero) {
                    IntPtr self = Marshal.ReadIntPtr(args, 0);
                    if (self != IntPtr.Zero) fallback = self;
                }
                // arguments[0] is `this`; argumentCount counts it too, so (text, size, style) is 4.
                // A disabled runtime is being terminated: skip the WPF measuring, the width is unused.
                if (!fatal && argCount >= 4 && !Disabled()) {
                    IntPtr text = Marshal.ReadIntPtr(args, IntPtr.Size);
                    IntPtr sizeValue = Marshal.ReadIntPtr(args, IntPtr.Size * 2);
                    IntPtr styleValue = Marshal.ReadIntPtr(args, IntPtr.Size * 3);
                    double size, style;
                    if (JsNumberToDouble(sizeValue, out size) == 0 && JsNumberToDouble(styleValue, out style) == 0) {
                        double width = MermaidEngine.MeasureText(Str(text), size, (int)style);
                        if (JsDoubleToNumber(width, out answer) != 0) answer = IntPtr.Zero;
                    }
                }
            } catch (Exception ex) { MermaidEngine.NoteFault(ex); answer = IntPtr.Zero; }
            // After an access violation no further chakra call is made: straight to the fallback.
            if (answer == IntPtr.Zero && !fatal) {
                try { if (JsDoubleToNumber(0, out answer) != 0) answer = IntPtr.Zero; } catch (Exception ex) { MermaidEngine.NoteFault(ex); answer = IntPtr.Zero; }
            }
            if (answer == IntPtr.Zero && !fatal) {
                try { if (JsGetUndefinedValue(out answer) != 0) answer = IntPtr.Zero; } catch (Exception ex) { MermaidEngine.NoteFault(ex); answer = IntPtr.Zero; }
            }
            return answer != IntPtr.Zero ? answer : fallback;
        }
        // True when the time limit has already disabled the render's runtime (one flag read in chakra).
        static bool Disabled() {
            IntPtr rt = hostRuntime;
            bool disabled;
            if (rt == IntPtr.Zero) return false;
            return JsIsRuntimeExecutionDisabled(rt, out disabled) == 0 && disabled;
        }
        internal static string Str(IntPtr value) {
            IntPtr str, chars;
            UIntPtr length;
            if (JsConvertValueToString(value, out str) != 0) return "";
            if (JsStringToPointer(str, out chars, out length) != 0) return "";
            return Marshal.PtrToStringUni(chars, (int)length.ToUInt32());
        }
    }
}
}
