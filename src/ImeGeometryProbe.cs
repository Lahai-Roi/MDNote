using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using ImeScreenPoint=MDNote.SourceEditor.ImeScreenPoint;

namespace MDNote {
// --ime-geometry-probe: where the inline IME composition element, the heading mark / quote indent, the candidate window anchor, the
// IMR_QUERYCHARPOSITION answer and the composition form land at a line start and mid-line (11-07 IME geometry task).
// Generated text only. The composition is drawn through the same SetImeInline path ImeProc uses; no real IME exists here, so the
// native candidate window itself is NOT observed: the rectangles PlaceImeCandidate / AnswerCharPosition compute are read instead
// (SourceEditor.ImeCandidateGeometry / ImeCharPositionClient), without calling ImmSetCandidateWindow.
internal static class ImeGeometryProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [StructLayout(LayoutKind.Sequential)] struct Form{public int Style,X,Y,Left,Top,Right,Bottom;}
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hwnd,IntPtr context);
    [DllImport("imm32.dll")] static extern bool ImmGetCompositionWindow(IntPtr context,out Form form);
    [DllImport("imm32.dll")] static extern bool ImmSetCompositionWindow(IntPtr context,ref Form form);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref SourceEditor.ImeScreenPoint point);
    const int WM_IME_STARTCOMPOSITION=0x010D,WM_IME_ENDCOMPOSITION=0x010E;
    static readonly StringBuilder report=new StringBuilder();
    static bool all=true;static int checks,failures;static string outFolder;

    internal static int Run(string outDir){
        outFolder=Path.GetFullPath(outDir);Directory.CreateDirectory(outFolder);
        string pass=Path.Combine(outFolder,"ime-geometry-probe-PASS.txt"),fail=Path.Combine(outFolder,"ime-geometry-probe-FAIL.txt");
        if(File.Exists(pass))File.Delete(pass);if(File.Exists(fail))File.Delete(fail);
        bool oldSelfTest=Program.IsSelfTest,oldLive=ProbeDefaults.Live;
        try{
            Program.IsSelfTest=true;ProbeDefaults.Live=true;
            if(System.Windows.Application.Current==null)new System.Windows.Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            System.Windows.Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            // name, text, no-reveal (basic) view, caret offset, composition, korean layout rules
            RunCase("A basic: lazy quote continuation line start","> 인용 첫 줄\n이어진 줄\n",true,Offset("> 인용 첫 줄\n이어진 줄\n","이어진"),"가",false);
            RunCase("A2 live: lazy quote continuation line start","> 인용 첫 줄\n이어진 줄\n",false,Offset("> 인용 첫 줄\n이어진 줄\n","이어진"),"가",false);
            RunCase("B basic: setext heading line 1 start","제목 줄\n===\n",true,0,"가",false);
            RunCase("B2 live: setext heading line 1 start","제목 줄\n===\n",false,0,"가",false);
            RunCase("P basic: plain paragraph line start","첫 줄\n\n본문 한 줄입니다\n",true,Offset("첫 줄\n\n본문 한 줄입니다\n","본문"),"가",false);
            RunCase("C basic: heading text start (after hidden #), korean","# 제목\n\n본문\n",true,2,"한",true);
            RunCase("C2 live: heading text start (after # ), korean","# 제목\n\n본문\n",false,2,"한",true);
            RunCase("M basic: mid-line","가나다 라마바\n",true,3,"한",false);
            RunCase("M2 basic: mid-line in quote","> 가나다 라마바\n",true,6,"한",false);
            RunReattach("R1 reattach lazy quote","> 인용 첫 줄\n이어진 줄\n",Offset("> 인용 첫 줄\n이어진 줄\n","이어진"),"가");
            RunReattach("R2 reattach setext","제목 줄\n===\n",0,"가");
            RunHeadingMarkBody();
        }catch(Exception ex){Check("exception",false,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{Program.IsSelfTest=oldSelfTest;ProbeDefaults.Live=oldLive;SourceEditor.KoreanLayoutOverride=null;}
        File.WriteAllText(Path.Combine(outFolder,"ime-geometry-probe.txt"),report.ToString(),new UTF8Encoding(false));
        File.WriteAllText(all?pass:fail,(all?"PASS":"FAIL")+" checks="+checks+" failures="+failures+"\n",new UTF8Encoding(false));
        return all?0:1;
    }
    static int Offset(string text,string at){return text.IndexOf(at,StringComparison.Ordinal);}
    static void Check(string name,bool ok,string detail){checks++;if(!ok)failures++;all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n');}
    static void Info(string text){report.Append(text).Append('\n');}
    static string N(double value){return Double.IsNaN(value)?"NaN":value.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture);}
    static void Pump(){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));}
    static void PumpAll(){Pump();Pump();}

    // What one build of the caret's visual line holds.
    sealed class Snap {
        internal double ImeX=Double.NaN,TextX=Double.NaN,MarkX=Double.NaN,ImeWidth=Double.NaN;
        internal int ImeColumn=-1,ImeIndex=-1;internal double LineBottom=Double.NaN,LineTop=Double.NaN;
    }
    static Snap Take(string label,SourceEditor editor,int line){
        var view=editor.TextArea.TextView;view.EnsureVisualLines();
        var visual=view.GetVisualLine(line);var snap=new Snap();
        if(visual==null||visual.IsDisposed){Info("  "+label+": no visual line");return snap;}
        Info("  "+label+" elements of line "+line+" (visual length "+visual.VisualLength+", height "+N(visual.Height)+"):");
        var elements=visual.Elements;
        for(int i=0;i<elements.Count;i++){
            var element=elements[i];
            double x=visual.GetVisualPosition(element.VisualColumn,VisualYPosition.LineTop).X-view.HorizontalOffset;
            double next=i+1<elements.Count?visual.GetVisualPosition(elements[i+1].VisualColumn,VisualYPosition.LineTop).X-view.HorizontalOffset:Double.NaN;
            Info("    ["+i+"] "+element.GetType().Name+" vc="+element.VisualColumn+" vlen="+element.VisualLength+" dlen="+element.DocumentLength+" rto="+element.RelativeTextOffset+" x="+N(x)+" w~"+N(next-x));
            if(element is ImeInlineElement&&snap.ImeIndex<0){snap.ImeIndex=i;snap.ImeX=x;snap.ImeColumn=element.VisualColumn;snap.ImeWidth=next-x;}
            if(element.GetType().Name=="VisualLineText"&&Double.IsNaN(snap.TextX))snap.TextX=x;
        }
        // The heading mark's slot as BlockRenderer.DrawHeadingMark finds it.
        for(int i=0;i<elements.Count&&i<24;i++){
            var hidden=elements[i] as MarkdownLivePreview.InvisibleElement;
            if(hidden!=null){if(hidden.Gap>0){snap.MarkX=visual.GetVisualPosition(hidden.VisualColumn,VisualYPosition.LineTop).X-view.HorizontalOffset;break;}continue;}
            var marker=elements[i] as MarkdownLivePreview.MarkerElement;
            if(marker!=null&&marker.IsHeadingMark){snap.MarkX=visual.GetVisualPosition(marker.VisualColumn,VisualYPosition.LineTop).X-view.HorizontalOffset;break;}
        }
        snap.LineTop=visual.VisualTop-view.VerticalOffset;snap.LineBottom=visual.VisualTop+visual.Height-view.VerticalOffset;
        Info("  "+label+" ime x="+N(snap.ImeX)+", first text x="+N(snap.TextX)+", heading mark x="+N(snap.MarkX));
        return snap;
    }
    static Point Dev(SourceEditor editor,HwndSource source,double x,double y){
        var view=editor.TextArea.TextView;
        Point root=view.TranslatePoint(new Point(x,y),source.RootVisual as UIElement);
        return source.CompositionTarget.TransformToDevice.Transform(root);
    }
    static bool ReadForm(IntPtr hwnd,out Form form){
        form=new Form();IntPtr context=ImmGetContext(hwnd);if(context==IntPtr.Zero)return false;
        try{return ImmGetCompositionWindow(context,out form);}finally{ImmReleaseContext(hwnd,context);}
    }
    static string FormText(IntPtr hwnd){Form form;if(!ReadForm(hwnd,out form))return "(no IME context)";return "style=0x"+form.Style.ToString("X")+" pt="+form.X+","+form.Y+" rc="+form.Left+","+form.Top+","+form.Right+","+form.Bottom;}
    static object Call(object target,string name,params object[] args){
        var method=target.GetType().GetMethod(name,Members);if(method==null)throw new MissingMemberException("probe: no method "+name);
        return method.Invoke(target,args);
    }
    static void Shot(FrameworkElement element,string name){
        try{
            var bitmap=new RenderTargetBitmap((int)Math.Max(1,element.ActualWidth),(int)Math.Max(1,element.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(outFolder,name+".png")))encoder.Save(stream);
        }catch(Exception ex){Info("  shot "+name+" failed: "+ex.Message);}
    }
    static string Slug(string name){var text=new StringBuilder();foreach(char c in name)text.Append(Char.IsLetterOrDigit(c)?c:'-');return text.ToString();}

    static void RunCase(string name,string text,bool hide,int offset,string composition,bool korean){
        Info("");Info("=== "+name+" (hide markers="+hide+", offset="+offset+", composition="+composition+") ===");
        var editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        var window=new Window {Content=editor,Width=700,Height=450,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=-20000};
        MarkdownLivePreview.Session session=null;
        try{
            window.Show();Pump();
            session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=hide});
            editor.Text=text;editor.Select(offset,0);PumpAll();
            for(int attempt=0;attempt<12&&!editor.TextArea.IsKeyboardFocused;attempt++){
                window.Activate();if(attempt==4)SetForegroundWindow(new WindowInteropHelper(window).Handle);
                editor.TextArea.Focus();System.Windows.Input.Keyboard.Focus(editor.TextArea);Pump();
            }
            bool focused=editor.TextArea.IsKeyboardFocused;
            var source=PresentationSource.FromVisual(editor.TextArea) as HwndSource;
            if(source==null){Check(name,false,"no window handle");return;}
            IntPtr hwnd=source.Handle;
            Info("keyboard focus="+focused+", imeSource hooked="+(typeof(SourceEditor).GetField("imeSource",Members).GetValue(editor)!=null));
            int caretLine=editor.Document.GetLocation(offset).Line;
            editor.Select(offset,0);PumpAll();
            Snap before=Take("before",editor,caretLine);
            var view=editor.TextArea.TextView;
            Info("  caret visual column before="+editor.TextArea.Caret.VisualColumn);
            Shot(editor,Slug(name)+"-before");
            // The composition: the inline string, as ImeProc's WM_IME_COMPOSITION handler hands it to the editor.
            if(korean)SourceEditor.KoreanLayoutOverride=delegate{return true;};
            var inline=typeof(SourceEditor).GetMethod("SetImeInline",Members,null,new Type[]{typeof(string)},null);
            inline.Invoke(editor,new object[]{composition});PumpAll();
            Snap during=Take("during",editor,caretLine);
            Shot(editor,Slug(name)+"-during");
            // The generator order the line was built with.
            var generators=view.ElementGenerators;object ime=typeof(SourceEditor).GetField("imeInline",Members).GetValue(editor);
            var list=new StringBuilder();int slot=0;bool lead=true;
            for(int i=0;i<generators.Count;i++){
                var g=generators[i];bool isIme=Object.ReferenceEquals(g,ime);
                list.Append(i).Append(':').Append(g.GetType().Name).Append(isIme?"(IME)":"").Append(' ');
                if(!isIme&&lead&&(g is MarkdownLivePreview.StrutGenerator||g is MarkdownLivePreview.LazyQuoteGenerator))slot++;else if(!isIme)lead=false;
            }
            int index=generators.IndexOf((VisualLineElementGenerator)ime);
            Info("  generators: "+list.ToString().TrimEnd());
            Check(name+" IME generator sits right behind the zero-length lead generators",index==slot,"index="+index+", expected slot="+slot);
            int startColumn=(int)Call(editor,"ImeStartColumn");
            Info("  ImeStartColumn="+startColumn+", IME element column="+during.ImeColumn+", caret visual column during="+editor.TextArea.Caret.VisualColumn+", caret offset="+editor.CaretOffset);
            // Where the string must start: where the text (or the caret's character) started before the composition.
            double wantX=before.TextX;
            if(offset>0&&!Double.IsNaN(before.TextX)){
                // mid-line / after a hidden prefix: the caret's own x before the composition.
                var location=editor.Document.GetLocation(offset);
                var position=new ICSharpCode.AvalonEdit.TextViewPosition(location);
                wantX=view.GetVisualPosition(position,VisualYPosition.LineTop).X-view.HorizontalOffset;
            }
            if(offset==editor.Document.GetLineByOffset(offset).Offset&&!Double.IsNaN(before.TextX))wantX=before.TextX;
            Info("  expected composition start x="+N(wantX)+" (before the composition the caret/text was drawn there)");
            Check(name+" IME element starts where the caret/text started",!Double.IsNaN(during.ImeX)&&Math.Abs(during.ImeX-wantX)<=1,"ime x="+N(during.ImeX)+", expected="+N(wantX));
            if(!Double.IsNaN(before.MarkX))Check(name+" heading mark slot does not move",!Double.IsNaN(during.MarkX)&&Math.Abs(during.MarkX-before.MarkX)<=0.5,"mark x "+N(before.MarkX)+" -> "+N(during.MarkX));
            // The geometry handed to the IME (device pixels of the window's client area).
            Point anchor;Rect exclude;bool got=editor.ImeCandidateGeometry(source,out anchor,out exclude);
            Point wantStart=Dev(editor,source,during.ImeX,during.LineTop),wantBottom=Dev(editor,source,during.ImeX,during.LineBottom);
            Info("  candidate anchor="+(got?N(anchor.X)+","+N(anchor.Y):"none")+" exclude="+(got?N(exclude.Left)+","+N(exclude.Top)+","+N(exclude.Right)+","+N(exclude.Bottom):"none")+"; ime element start dev="+N(wantStart.X)+","+N(wantStart.Y)+" line bottom dev y="+N(wantBottom.Y));
            Check(name+" candidate exclude rect starts at the composition start",got&&Math.Abs(exclude.Left-wantStart.X)<=1.5,"exclude.left="+(got?N(exclude.Left):"-")+", start="+N(wantStart.X));
            Check(name+" candidate anchor is below the line at the composition start",got&&Math.Abs(anchor.X-wantStart.X)<=1.5&&Math.Abs(anchor.Y-wantBottom.Y)<=1.5,"anchor="+(got?N(anchor.X)+","+N(anchor.Y):"-")+", want x="+N(wantStart.X)+" y="+N(wantBottom.Y));
            Check(name+" candidate rect is inside the window (non-zero, client coordinates)",got&&anchor.X>=0&&anchor.Y>0&&exclude.Width>0&&exclude.Height>0&&anchor.X<=source.CompositionTarget.TransformToDevice.M11*((FrameworkElement)source.RootVisual).ActualWidth&&anchor.Y<=source.CompositionTarget.TransformToDevice.M22*((FrameworkElement)source.RootVisual).ActualHeight,"anchor="+(got?N(anchor.X)+","+N(anchor.Y):"-"));
            SourceEditor.ImeScreenPoint at,docTop,docBottom;int height;bool saved;
            bool answered=editor.ImeCharPositionClient(source,0,out at,out height,out docTop,out docBottom,out saved);
            SourceEditor.ImeScreenPoint screen=at;if(answered)ClientToScreen(hwnd,ref screen);
            Info("  IMR_QUERYCHARPOSITION answer: client="+(answered?at.X+","+at.Y:"none")+" screen="+(answered?screen.X+","+screen.Y:"none")+" height="+height+" saved="+saved+" doc client="+docTop.X+","+docTop.Y+".."+docBottom.X+","+docBottom.Y);
            Check(name+" char position answer is the composition start",answered&&!saved&&Math.Abs(at.X-wantStart.X)<=1.5&&Math.Abs(at.Y-wantStart.Y)<=1.5,"answer="+(answered?at.X+","+at.Y:"-")+", start="+N(wantStart.X)+","+N(wantStart.Y));
            // The composition form the live preview writes (LiveCompositionGeometry.Apply): informational, the Korean layout parks it.
            double size;Typeface face;Point intended=LiveCompositionGeometry.Position(editor,out size,out face);
            Point intendedDev=Double.IsNaN(intended.X)?intended:LiveCompositionGeometry.ToDevice(editor,source,intended);
            Info("  live composition point (device)="+N(intendedDev.X)+","+N(intendedDev.Y)+" (composition start dev="+N(wantStart.X)+","+N(wantStart.Y)+", end of string dev x~"+N(wantStart.X+during.ImeWidth*source.CompositionTarget.TransformToDevice.M11)+")");
            string formBefore=FormText(hwnd);LiveCompositionGeometry.Apply(editor,source);
            Info("  IME form before Apply: "+formBefore+"; after LiveCompositionGeometry.Apply: "+FormText(hwnd));
            // The form point is the composition START (x) on the start row (y between the line's top and bottom), for every language; the end of the string is not accepted.
            Check(name+" live composition point is the composition start, not the end of the string",!Double.IsNaN(intendedDev.X)&&Math.Abs(intendedDev.X-wantStart.X)<=1.5&&intendedDev.Y>=wantStart.Y-1.5&&intendedDev.Y<=wantBottom.Y+1.5,
                "form point="+N(intendedDev.X)+","+N(intendedDev.Y)+", start="+N(wantStart.X)+","+N(wantStart.Y)+", line bottom y="+N(wantBottom.Y));
            Form applied;bool appliedRead=ReadForm(hwnd,out applied);
            if(appliedRead)Check(name+" IME form after Apply sits at the composition start",Math.Abs(applied.X-wantStart.X)<=1.5&&applied.Y>=wantStart.Y-1.5&&applied.Y<=wantBottom.Y+1.5,"form="+FormText(hwnd)+", start="+N(wantStart.X)+","+N(wantStart.Y));
            else Info("  (no IME context in this run: form not read)");
            if(korean){
                if(focused)KoreanFlow(name,editor,hwnd,source,composition,wantStart,wantBottom);
                else Info("  korean form flow skipped: no keyboard focus in this run");
            }
            inline.Invoke(editor,new object[]{(string)null});
            // Right after the string ends (a commit or the Hanja key): the lines are being rebuilt, and the IME asks for the position now.
            SourceEditor.ImeScreenPoint at2,top2,bottom2;int height2;bool saved2;
            bool after=editor.ImeCharPositionClient(source,0,out at2,out height2,out top2,out bottom2,out saved2);
            Info("  answer right after the string ended (lines rebuilding): "+(after?at2.X+","+at2.Y:"none")+" saved form used="+saved2);
            Check(name+" answer right after the string ended is still the composition start",after&&!saved2&&Math.Abs(at2.X-wantStart.X)<=1.5&&Math.Abs(at2.Y-wantStart.Y)<=1.5,"answer="+(after?at2.X+","+at2.Y:"-")+", start="+N(wantStart.X)+","+N(wantStart.Y));
            PumpAll();
        }catch(Exception ex){Check(name+" exception",false,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            SourceEditor.KoreanLayoutOverride=null;
            try{editor.ResetCompositionSimulation();}catch(Exception){}
            if(session!=null)session.Dispose();
            window.Close();
        }
    }

    // A composition is drawn, then the live preview is attached again (what a view / mode switch does: its lead generators are inserted at the list start, ahead of ours)
    // and its settings change; no further SetImeInline arrives. The string must still sit behind the quote indent / heading mark slot once the next lines are built.
    static MarkdownLivePreview.Settings Settings(bool hide){return new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=hide};}
    static string GeneratorNames(SourceEditor editor){
        var text=new StringBuilder();var generators=editor.TextArea.TextView.ElementGenerators;
        for(int i=0;i<generators.Count;i++){if(i>0)text.Append(',');text.Append(generators[i].GetType().Name);}
        return text.ToString();
    }
    static void RunReattach(string name,string text,int offset,string composition){
        Info("");Info("=== "+name+" (composition="+composition+", offset="+offset+") ===");
        var editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        var window=new Window {Content=editor,Width=700,Height=450,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=-20000};
        MarkdownLivePreview.Session session=null;
        try{
            window.Show();Pump();
            session=MarkdownLivePreview.Attach(editor,Settings(true));
            editor.Text=text;editor.Select(offset,0);PumpAll();
            for(int attempt=0;attempt<12&&!editor.TextArea.IsKeyboardFocused;attempt++){
                window.Activate();if(attempt==4)SetForegroundWindow(new WindowInteropHelper(window).Handle);
                editor.TextArea.Focus();System.Windows.Input.Keyboard.Focus(editor.TextArea);Pump();
            }
            int caretLine=editor.Document.GetLocation(offset).Line;
            Snap before=Take("before",editor,caretLine);
            var inline=typeof(SourceEditor).GetMethod("SetImeInline",Members,null,new Type[]{typeof(string)},null);
            inline.Invoke(editor,new object[]{composition});PumpAll();
            Info("  generators with the string drawn: "+GeneratorNames(editor));
            Snap drawn=Take("drawn",editor,caretLine);
            Check(name+" string drawn behind the lead elements first",Math.Abs(drawn.ImeX-before.TextX)<=1,"ime x="+N(drawn.ImeX)+", text start="+N(before.TextX));
            // Attach again: Dispose removes the live preview's generators, Attach inserts them at 0,1,2 (ahead of ours, wherever it was).
            session.Dispose();session=MarkdownLivePreview.Attach(editor,Settings(true));
            Info("  generators right after the live preview was attached again: "+GeneratorNames(editor));
            PumpAll();PumpAll();
            Info("  generators after the next lines were built: "+GeneratorNames(editor));
            Snap again=Take("after re-attach",editor,caretLine);
            Check(name+" string still behind the lead elements after the live preview was attached again",!Double.IsNaN(again.ImeX)&&Math.Abs(again.ImeX-before.TextX)<=1,"ime x="+N(again.ImeX)+", text start="+N(before.TextX)+", generators="+GeneratorNames(editor));
            if(!Double.IsNaN(before.MarkX))Check(name+" heading mark slot does not move after re-attach",Math.Abs(again.MarkX-before.MarkX)<=0.5,"mark x "+N(before.MarkX)+" -> "+N(again.MarkX));
            // The settings change of the same session (theme / zoom / marker toggle) rebuilds every line too.
            session.Update(Settings(false));PumpAll();session.Update(Settings(true));PumpAll();PumpAll();
            Snap updated=Take("after settings updates",editor,caretLine);
            Check(name+" string still behind the lead elements after settings updates",!Double.IsNaN(updated.ImeX)&&Math.Abs(updated.ImeX-before.TextX)<=1,"ime x="+N(updated.ImeX)+", text start="+N(before.TextX)+", generators="+GeneratorNames(editor));
            inline.Invoke(editor,new object[]{(string)null});PumpAll();
        }catch(Exception ex){Check(name+" exception",false,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            try{editor.ResetCompositionSimulation();}catch(Exception){}
            if(session!=null)session.Dispose();
            window.Close();
        }
    }

    // The heading slash bar's body (11-06, three user reports: the bar sat above the letters, was not
    // centred on the glyph body, and changed height while a Korean composition ran). The product's
    // HeadingMark.Measure clamps each glyph's top to the body of the first row (U+ADF8's black-box top
    // for a Hangul syllable, whose vowel strokes rise above the rest; the capital height for other
    // glyphs) and its bottom to U+AC00's depth under the baseline (so a Latin descender is cut), and
    // DrawHeadingMark draws top=round(baseline-above), bottom=round(baseline+below) from those values.
    // The check re-measures the glyph box from the first glyph run's own typeface tables, computes the
    // bar rectangle the same way, and compares the two with a 1.5 dip tolerance.
    // Depends on: the HangulTop / CapsTop clamp in Measure (MarkdownLivePreview.cs). Removing that
    // clamp raises the bar to the tallest glyph stroke: `above` becomes the raw box top (the 가 vowel
    // stroke), grows past the tolerance, and this check fails. The bottom clamp (HangulDepth vs the
    // baseline) is guarded by the same rectangle's bottom edge.
    static void RunHeadingMarkBody(){
        Info("");Info("=== H heading mark body (11-06 user reports) ===");
        var editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=32,WordWrap=true};
        var window=new Window {Content=editor,Width=800,Height=500,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=-20000};
        MarkdownLivePreview.Session session=null;
        try{
            window.Show();Pump();
            session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=32,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=true});
            // Two heading sizes so the scale is covered: H1 (2 em) with a Hangul body and H3 (1.3 em)
            // with a Latin body (its descender must not reach below the baseline).
            editor.Text="# 가가가\n\n### Wg\n\n본문\n";editor.Select(0,0);PumpAll();
            window.UpdateLayout();PumpAll();
            var view=editor.TextArea.TextView;view.EnsureVisualLines();
            CheckHeadingBody(view,1,"가가가");
            CheckHeadingBody(view,3,"Wg");
        }catch(Exception ex){Check("H heading mark body exception",false,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            if(session!=null)session.Dispose();
            window.Close();
        }
    }
    static void CheckHeadingBody(TextView view,int lineNumber,string headingText){
        string name="H line "+lineNumber+" 11-06 heading slash bar spans the glyph body";
        VisualLine visual=view.GetVisualLine(lineNumber);
        if(visual==null||visual.IsDisposed){Check(name,false,"no visual line "+lineNumber);return;}
        // The mark slot, found the way DrawHeadingMark finds it.
        VisualLineElement at=null;
        for(int i=0;i<visual.Elements.Count&&i<24;i++){
            var element=visual.Elements[i];
            var hidden=element as MarkdownLivePreview.InvisibleElement;
            if(hidden!=null){if(hidden.Gap>0){at=element;break;}continue;}
            var marker=element as MarkdownLivePreview.MarkerElement;
            if(marker!=null&&marker.IsHeadingMark){at=element;break;}
        }
        if(at==null){Check(name,false,"no heading mark slot on line "+lineNumber);return;}
        double dip=VisualTreeHelper.GetDpi(view).PixelsPerDip;
        double baseline=visual.GetVisualPosition(at.VisualColumn,VisualYPosition.Baseline).Y-view.VerticalOffset;
        double above,below;
        if(!HeadingMark.Ink(visual,headingText,out above,out below)){Check(name,false,"HeadingMark.Ink has no glyph metrics for line "+lineNumber);return;}
        // The bar rectangle DrawHeadingMark hands to MarkGeometry: device pixels rounded, read in dips.
        double barTop=Math.Round((baseline-above)*dip)/dip,barBottom=Math.Round((baseline+below)*dip)/dip;
        // The glyph body, measured here from the first glyph run's typeface: a Hangul syllable's top is
        // U+ADF8's black box (its vertical vowel strokes rise above the consonant), any other glyph's is
        // the capital height; a Hangul line's bottom is U+AC00's depth under the baseline, and a Latin
        // line's is the baseline itself (no descender).
        double wantAbove=Double.NaN,wantBelow=0;bool cjk=false;
        if(visual.TextLines!=null&&visual.TextLines.Count>0){
            IEnumerable<System.Windows.Media.TextFormatting.IndexedGlyphRun> runs=visual.TextLines[0].GetIndexedGlyphRuns();
            if(runs!=null)foreach(System.Windows.Media.TextFormatting.IndexedGlyphRun indexed in runs){
                GlyphRun run=indexed.GlyphRun;
                if(run==null||run.GlyphTypeface==null||run.GlyphIndices==null||run.Characters==null)continue;
                GlyphTypeface face=run.GlyphTypeface;double em=run.FontRenderingEmSize;
                for(int g=0;g<run.GlyphIndices.Count&&g<run.Characters.Count;g++){
                    ushort index=run.GlyphIndices[g];char c=run.Characters[g];bool hangul=c>='가'&&c<='힣';
                    if(hangul)cjk=true;
                    double advance,boxTop,boxBottom,low;
                    if(!face.AdvanceHeights.TryGetValue(index,out advance)||!face.TopSideBearings.TryGetValue(index,out boxTop)||!face.BottomSideBearings.TryGetValue(index,out boxBottom)||!face.DistancesFromHorizontalBaselineToBlackBoxBottom.TryGetValue(index,out low))continue;
                    double box=advance-boxTop-boxBottom;
                    if(box<=0.02)continue;
                    double raw=(box-low)*em;
                    if(raw<=0||raw>em*2)continue;
                    // The body top the fix defines: the glyph's own box, cut at U+ADF8's top for a
                    // Hangul syllable and at the capital height otherwise.
                    double body=hangul?HangulTop(face):CapsTop(face);
                    double glyphTop=Double.IsNaN(body)?raw:Math.Min(raw,body*em);
                    if(Double.IsNaN(wantAbove)||glyphTop>wantAbove)wantAbove=glyphTop;
                }
                double depth=HangulDepth(face);
                if(cjk&&!Double.IsNaN(depth)){double reach=depth*em;if(reach>wantBelow)wantBelow=reach;}
            }
        }
        bool ok=!Double.IsNaN(wantAbove)&&Math.Abs(above-wantAbove)<=1.5&&Math.Abs(below-wantBelow)<=1.5;
        Check(name,ok,"above="+N(above)+" want="+N(wantAbove)+" below="+N(below)+" want="+N(wantBelow)
            +" bar="+N(barTop)+".."+N(barBottom)+" baseline="+N(baseline)+" dip="+N(dip)+" text="+headingText);
    }
    // U+ADF8's black-box top above the baseline in em (the body top a Hangul heading's bar uses): the
    // same table read HeadingMark.Measure performs. NaN when the typeface lacks the glyph.
    static double HangulTop(GlyphTypeface face){
        try{
            ushort index;double advance,bearingTop,bearingBottom,low;
            if(face.CharacterToGlyphMap.TryGetValue(0xADF8,out index)&&face.AdvanceHeights.TryGetValue(index,out advance)&&face.TopSideBearings.TryGetValue(index,out bearingTop)&&face.BottomSideBearings.TryGetValue(index,out bearingBottom)&&face.DistancesFromHorizontalBaselineToBlackBoxBottom.TryGetValue(index,out low)){
                double value=advance-bearingTop-bearingBottom-low;
                if(value>0.3&&value<1.5)return value;
            }
        }catch(Exception){}
        return Double.NaN;
    }
    static double CapsTop(GlyphTypeface face){
        try{double caps=face.CapsHeight;return caps>=0.55&&caps<=0.85?caps:Double.NaN;}catch(Exception){return Double.NaN;}
    }
    // U+AC00's black-box bottom below the baseline in em, 0 when it sits on it; NaN when missing.
    static double HangulDepth(GlyphTypeface face){
        try{
            ushort index;double bottom;
            if(face.CharacterToGlyphMap.TryGetValue(0xAC00,out index)&&face.DistancesFromHorizontalBaselineToBlackBoxBottom.TryGetValue(index,out bottom))return Math.Max(0,bottom);
        }catch(Exception){}
        return Double.NaN;
    }

    // The Korean path (11-08): the form is parked off-screen while typing (an on-screen form showed the IME's own syllable box over the inline run, user video), START parks it,
    // the drawn string leaves it parked, AvalonEdit's own write at the caret is parked again, and the lone Hanja request gives it back at the composition start.
    static void KoreanFlow(string name,SourceEditor editor,IntPtr hwnd,HwndSource source,string composition,Point wantStart,Point wantBottom){
        Info("  -- korean form flow (real WM_IME_STARTCOMPOSITION through the window hook) --");
        Info("  form at rest before START: "+FormText(hwnd));
        SendMessage(hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero);PumpAll();
        Info("  form after START: "+FormText(hwnd));
        Form atStart;bool readStart=ReadForm(hwnd,out atStart);
        Check(name+" form is parked off screen after START",readStart&&atStart.X<=-10000,"form="+FormText(hwnd));
        var inline=typeof(SourceEditor).GetMethod("SetImeInline",Members,null,new Type[]{typeof(string)},null);
        inline.Invoke(editor,new object[]{composition});PumpAll();
        Info("  form with the string drawn: "+FormText(hwnd));
        Form drawn;bool readDrawn=ReadForm(hwnd,out drawn);
        Check(name+" form stays parked while the string is drawn",readDrawn&&drawn.X<=-10000,
            "form="+FormText(hwnd)+", start="+N(wantStart.X)+","+N(wantStart.Y)+", line bottom y="+N(wantBottom.Y));
        // AvalonEdit's caret placement writes the form at the caret, after the string; the IMN_SETCOMPOSITIONWINDOW it causes (sent here by hand) puts the start back.
        IntPtr context=ImmGetContext(hwnd);
        if(context!=IntPtr.Zero){
            try{Form atCaret=drawn;atCaret.X=(int)wantStart.X+40;atCaret.Y=(int)wantStart.Y;ImmSetCompositionWindow(context,ref atCaret);}finally{ImmReleaseContext(hwnd,context);}
            SendMessage(hwnd,0x0282,new IntPtr(0x000B),IntPtr.Zero);PumpAll();
            Form again;bool readAgain=ReadForm(hwnd,out again);
            Check(name+" a form written at the caret is parked again",readAgain&&again.X<=-10000,"form="+FormText(hwnd)+", start="+N(wantStart.X)+","+N(wantStart.Y));
        }
        /* The Hanja key (11-08 user: the Hanja / symbol list opened at the screen's top-left): the key
           never reaches WPF (the IME eats it), so the product installs a WH_KEYBOARD thread hook when a
           Korean composition starts (SourceEditor.WatchHanjaKey, called from the WM_IME_STARTCOMPOSITION
           branch) and answers VK_HANJA key-down with the parked form given back at the composition start
           (OnHanjaKeyHook -> RestoreParkedForm) before the IME opens its list.
           Depends on: the WatchHanjaKey call and the imeHanjaHook field it fills, and the
           RestoreParkedForm call inside OnHanjaKeyHook. Reverting either leaves the probe's Simulated
           VK_HANJA unanswered and the form at X=-32000, so the check below fails. */
        FieldInfo hookField=typeof(SourceEditor).GetField("imeHanjaHook",Members);
        IntPtr hanjaHook=hookField==null?IntPtr.Zero:(IntPtr)hookField.GetValue(null);
        Check(name+" 11-08 hanja key hook is installed for the korean composition",hookField!=null&&hanjaHook!=IntPtr.Zero,"hook=0x"+hanjaHook.ToInt64().ToString("X"));
        Form parked;bool readParked=ReadForm(hwnd,out parked);
        Check(name+" 11-08 form is parked before the hanja key",readParked&&parked.X<=-10000,"form="+FormText(hwnd)+", start="+N(wantStart.X)+","+N(wantStart.Y));
        var onHanja=typeof(SourceEditor).GetMethod("OnHanjaKeyHook",Members,null,new Type[]{typeof(int),typeof(IntPtr),typeof(IntPtr)},null);
        bool hanjaCalled=false;
        if(onHanja!=null){
            try{onHanja.Invoke(null,new object[]{0,new IntPtr(0x19),IntPtr.Zero});hanjaCalled=true;}
            catch(Exception){hanjaCalled=false;}
        }
        PumpAll();
        Form restored;bool readRestored=ReadForm(hwnd,out restored);
        Check(name+" 11-08 hanja key hook gives the parked form back at the composition start",hanjaCalled&&readRestored&&restored.X>-10000&&Math.Abs(restored.X-wantStart.X)<=1.5&&restored.Y>=wantStart.Y-1.5&&restored.Y<=wantBottom.Y+1.5,"form="+FormText(hwnd)+", expected="+N(wantStart.X)+","+N(wantStart.Y));
        SendMessage(hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero);PumpAll();
        Info("  form after END: "+FormText(hwnd));
        // The key never reaches WPF; the only sign is a lone IMR_QUERYCHARPOSITION long after the last composition message (sent for real through the window hook).
        SendMessage(hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero);PumpAll();
        inline.Invoke(editor,new object[]{composition});PumpAll();
        Info("  [lone request] form while composing: "+FormText(hwnd));
        System.Threading.Thread.Sleep(200);
        IntPtr buffer=Marshal.AllocHGlobal(36);
        try{
            for(int i=0;i<36;i+=4)Marshal.WriteInt32(buffer,i,0);
            Marshal.WriteInt32(buffer,0,36);
            IntPtr answer=SendMessage(hwnd,0x0288,new IntPtr(6),buffer);
            int px=Marshal.ReadInt32(buffer,8),py=Marshal.ReadInt32(buffer,12),lineHeight=Marshal.ReadInt32(buffer,16);
            ImeScreenPoint want=new ImeScreenPoint();want.X=(int)wantStart.X;want.Y=(int)wantStart.Y;ClientToScreen(hwnd,ref want);
            Info("  [lone request] result="+answer+" pt(screen)="+px+","+py+" height="+lineHeight+" expected screen="+want.X+","+want.Y+"; form now: "+FormText(hwnd));
            Check(name+" lone IMR_QUERYCHARPOSITION is answered with the composition start (screen coordinates)",answer.ToInt64()==1&&Math.Abs(px-want.X)<=1.5&&Math.Abs(py-want.Y)<=1.5,"result="+answer+", pt="+px+","+py+", expected="+want.X+","+want.Y);
            Form afterRequest;bool readAfter=ReadForm(hwnd,out afterRequest);
            Check(name+" form is still at the composition start after the lone request",readAfter&&afterRequest.X>-10000&&Math.Abs(afterRequest.X-wantStart.X)<=1.5&&afterRequest.Y>=wantStart.Y-1.5&&afterRequest.Y<=wantBottom.Y+1.5,"form="+FormText(hwnd)+", expected="+N(wantStart.X)+","+N(wantStart.Y));
        }finally{Marshal.FreeHGlobal(buffer);}
        SendMessage(hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero);PumpAll();
    }
}
}
