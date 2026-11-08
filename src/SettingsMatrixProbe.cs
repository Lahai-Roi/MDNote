using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
namespace MDNote {
// --settings-probe <folder>: after a base font, base size or line-spacing change, the live
// preview and the raw source view must keep their text, caret, reading position, number column
// and size box. Generated fixture only. One line per case:
// PASS|FAIL <view> <font> <size> <line%> <check> <detail>.
public partial class MainWindow {
    static Color ReviewInk(Brush brush){var solid=brush as SolidColorBrush;return solid==null?Colors.Transparent:solid.Color;}
    static Color ReviewRunInk(SourceEditor editor,int lineNumber,int relative){
        var view=editor.TextArea.TextView;view.EnsureVisualLines();var visual=view.GetVisualLine(lineNumber);
        if(visual!=null)foreach(var element in visual.Elements)if(relative>=element.RelativeTextOffset&&relative<element.RelativeTextOffset+element.DocumentLength)return ReviewInk(element.TextRunProperties.ForegroundBrush);
        return Colors.Transparent;
    }
    static void VerifyReviewSettings(Action<string,bool,string> check){
        MainWindow window=null;bool oldHide=hideMarkers;
        try{
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=1000,Height=720,Left=-20000,Top=-20000};window.Show();window.textMode=false;
            const string fixture="plain <span style=\"color:#123456\">explicit</span>\n# heading\n\n| head | value |\n| --- | --- |\n| body | <span style=\"color:#123456\">explicit</span> |\n";
            window.SetText(fixture);window.SetMode(true);
            Color chosen=Color.FromRgb(0xC0,0x30,0x60),explicitInk=Color.FromRgb(0x12,0x34,0x56);
            foreach(bool live in new[]{false,true})foreach(bool night in new[]{false,true}){
                window.SetLiveMode(live);window.SetDark(night);window.source.Select(0,0);
                window.customColor=chosen;window.RenderPreservingView();LongLineBench.Idle();window.UpdateLayout();LongLineBench.Idle();
                var session=window.livePreview;var row=session.Info(window.source.Document.GetLineByNumber(6)).Row;
                Color body=ReviewRunInk(window.source,1,0),span=ReviewRunInk(window.source,1,fixture.IndexOf("explicit",StringComparison.Ordinal));
                Color cell=row==null||row.Texts.Length==0?Colors.Transparent:ReviewInk(row.Texts[0].Default.ForegroundBrush);
                check("x8 default color MD "+live+"/"+night,body==chosen&&cell==chosen&&span==explicitInk,"body="+body+", cell="+cell+", explicit="+span);
                window.customColor=null;window.RenderPreservingView();LongLineBench.Idle();
                check("x8 default color reset "+live+"/"+night,ReviewRunInk(window.source,1,0)!=chosen&&ReviewInk(window.livePreview.CellDefaults[0].ForegroundBrush)!=chosen,"reset body="+ReviewRunInk(window.source,1,0));
            }
            var beforePalette=window.livePreview.Colors;
            window.customColor=chosen;window.UpdateLivePreview();
            check("x8 color-only Session.Update invalidates palette",!Object.ReferenceEquals(beforePalette,window.livePreview.Colors)&&ReviewInk(window.livePreview.CellDefaults[0].ForegroundBrush)==chosen,"cell="+ReviewInk(window.livePreview.CellDefaults[0].ForegroundBrush));
            var unchangedPalette=window.livePreview.Colors;window.UpdateLivePreview();
            check("x8 identical Session.Update keeps palette",Object.ReferenceEquals(unchangedPalette,window.livePreview.Colors),"same="+Object.ReferenceEquals(unchangedPalette,window.livePreview.Colors));
            window.customColor=null;
            window.SetLiveMode(false);window.SetDark(false);window.SetBaseFont("Malgun Gothic",16);LongLineBench.Idle();
            int version=window.viewTransitionVersion;
            window.source.SimulateImeCommit(1);
            window.Zoom(2);window.Zoom(2);window.SetDark(true);window.SetDark(false);window.SetDark(true);
            window.SetBaseFont("Consolas",20);window.Zoom(2);window.RenderPreservingView();window.RenderPreservingView();
            check("x8 view changes wait for composition",window.viewTransitionVersion==version&&window.fontSize==16&&!window.dark,"transition delta="+(window.viewTransitionVersion-version));
            window.source.TextArea.PerformTextInput("한");LongLineBench.Idle();window.UpdateLayout();LongLineBench.Idle();
            check("x8 queued view changes coalesce in order",window.viewTransitionVersion==version+1&&window.family=="Consolas"&&window.fontSize==22&&window.dark,"transition delta="+(window.viewTransitionVersion-version)+", font="+window.family+", size="+window.fontSize+", dark="+window.dark);
            window.source.SimulateImeCommit(1);window.Zoom(10);
            window.source.Document=new ICSharpCode.AvalonEdit.Document.TextDocument("new document");
            window.source.ResetCompositionSimulation();
            window.source.SimulateImeCommit(1);window.Zoom(2);
            window.source.TextArea.PerformTextInput("한");LongLineBench.Idle();
            check("x8 document swap drops stale view settings",window.fontSize==24&&window.pendingViewSettings==null,"size="+window.fontSize+", queued="+(window.pendingViewSettings!=null));
        }finally{
            hideMarkers=oldHide;
            if(window!=null){window.source.ResetCompositionSimulation();window.CaptureTab();foreach(var state in window.documents){state.Saved=state.Text;state.Recovered=false;}window.saved=window.text;window.Close();}
        }
    }
    // Settings merge across windows (P1-3), the word-wrap save (P3-24) and the F-key focus rule
    // (P1-7). The merge works on ViewPreferences snapshots only, so the user's real settings file is
    // never read or written.
    static void VerifyPreferenceMerge(string folder,Action<string,bool,string> check){
        const string a=@"C:\MDNote probe\one.md",b=@"C:\MDNote probe\two.md",c=@"C:\MDNote probe\three.md",d=@"C:\MDNote probe\four.md";
        var baseline=new ViewPreferences {Language="ko",Dark=false,Size=16,Family="Malgun Gothic",WordWrap=true,Recent=new List<string>{a,b}};
        var disk=new ViewPreferences {Language="ko",Dark=false,Size=16,Family="Malgun Gothic",WordWrap=true,Recent=new List<string>{a,b}};
        // Window A changes language, theme, size and wrap and opens a file.
        var windowA=new ViewPreferences {Language="en",Dark=true,Size=24,Family="Malgun Gothic",WordWrap=false,Recent=new List<string>{c,a,b}};
        ViewPreferences mergedA=ViewPreferences.MergePreferences(windowA,baseline,disk);
        check("merge: the changing window wins",mergedA.Language=="en"&&mergedA.Dark&&mergedA.Size==24&&!mergedA.WordWrap,"language="+mergedA.Language+", dark="+mergedA.Dark+", size="+mergedA.Size+", wrap="+mergedA.WordWrap);
        check("merge: its recent file comes first",mergedA.Recent.Count==3&&mergedA.Recent[0]==c&&mergedA.Recent.Contains(a)&&mergedA.Recent.Contains(b),"recent="+String.Join(",",mergedA.Recent.ToArray()));
        // Window B was opened before A's changes, so it still holds the old values; saving it must
        // keep A's values (the old code rewrote the whole snapshot).
        var windowB=new ViewPreferences {Language="ko",Dark=false,Size=16,Family="Consolas",WordWrap=true,Recent=new List<string>{d,a,b}};
        ViewPreferences mergedB=ViewPreferences.MergePreferences(windowB,baseline,mergedA);
        check("merge: another window's values survive",mergedB.Language=="en"&&mergedB.Dark&&mergedB.Size==24&&!mergedB.WordWrap&&mergedB.Family=="Consolas","language="+mergedB.Language+", dark="+mergedB.Dark+", size="+mergedB.Size+", wrap="+mergedB.WordWrap+", font="+mergedB.Family);
        check("merge: recents are a union, newest first",mergedB.Recent.Count==4&&mergedB.Recent[0]==d&&mergedB.Recent[1]==c&&mergedB.Recent.Contains(a)&&mergedB.Recent.Contains(b),"recent="+String.Join(",",mergedB.Recent.ToArray()));
        // Closing a window that changed nothing but its placement must not roll anything back.
        var closing=new ViewPreferences {Language="ko",Dark=false,Size=16,Family="Malgun Gothic",WordWrap=true,Recent=new List<string>{a,b},WindowPlacement="100|120|900|700|normal"};
        ViewPreferences mergedClose=ViewPreferences.MergePreferences(closing,baseline,mergedB);
        check("merge: close writes only its placement",mergedClose.WindowPlacement=="100|120|900|700|normal"&&mergedClose.Language=="en"&&mergedClose.Dark&&mergedClose.Size==24&&!mergedClose.WordWrap&&mergedClose.Family=="Consolas","placement="+mergedClose.WindowPlacement+", language="+mergedClose.Language+", dark="+mergedClose.Dark+", font="+mergedClose.Family);
        // 최근 파일 기록 삭제 in window A, then window B (still holding a, b) opens c and saves: a and b
        // stay cleared. A's off switch then wins over B, which never touched it (Argon review 11-07).
        var cleared=ViewPreferences.MergePreferences(new ViewPreferences {Language="ko",Recent=new List<string>()},new ViewPreferences {Language="ko",Recent=new List<string>{a,b}},new ViewPreferences {Language="ko",Recent=new List<string>{a,b}});
        int clearedCount=cleared.Recent.Count;/* the merge returns the disk snapshot it was given, changed in place */
        var reopened=ViewPreferences.MergePreferences(new ViewPreferences {Language="ko",Recent=new List<string>{c,a,b}},new ViewPreferences {Language="ko",Recent=new List<string>{a,b}},cleared);
        check("merge: another window's clear is not undone",clearedCount==0&&reopened.Recent.Count==1&&reopened.Recent[0]==c,"cleared="+clearedCount+" reopened="+String.Join(",",reopened.Recent.ToArray()));
        var off=ViewPreferences.MergePreferences(new ViewPreferences {Language="ko",RecentOff=true,Recent=new List<string>()},new ViewPreferences {Language="ko",Recent=new List<string>{a}},new ViewPreferences {Language="ko",Recent=new List<string>{a}});
        bool offWritten=off.RecentOff;
        var other=ViewPreferences.MergePreferences(new ViewPreferences {Language="ko",Recent=new List<string>{d,a}},new ViewPreferences {Language="ko",Recent=new List<string>{a}},off);
        check("merge: another window's off switch holds",offWritten&&other.RecentOff&&other.Recent.Count==0&&other.Serialize().IndexOf("<Recent>",StringComparison.Ordinal)<0,"off="+off.RecentOff+" other="+other.RecentOff+" recent="+other.Recent.Count);
        // Word wrap round-trips through the file, and a file from an older build still wraps.
        string path=Path.Combine(folder,"settings-merge-roundtrip.xml");bool unreadable;
        ViewPreferences.Save(path,new ViewPreferences {WordWrap=false}.Serialize());
        ViewPreferences reread=ViewPreferences.Load(path,out unreadable);
        check("wrap: saved off and loaded off",!unreadable&&!reread.WordWrap,"unreadable="+unreadable+", wrap="+reread.WordWrap);
        ViewPreferences.Save(path,new ViewPreferences {WordWrap=true}.Serialize());
        reread=ViewPreferences.Load(path,out unreadable);
        check("wrap: saved on and loaded on",!unreadable&&reread.WordWrap,"unreadable="+unreadable+", wrap="+reread.WordWrap);
        string legacy=Path.Combine(folder,"settings-legacy.xml");
        File.WriteAllText(legacy,"<?xml version=\"1.0\" encoding=\"utf-8\"?><MDNote font=\"Malgun Gothic\" size=\"16\" dark=\"false\" />",new UTF8Encoding(false));
        reread=ViewPreferences.Load(legacy,out unreadable);
        check("wrap: a file without the attribute keeps wrapping",!unreadable&&reread.WordWrap,"unreadable="+unreadable+", wrap="+reread.WordWrap);
    }
    // F1..F12 must stay in a focused input field. The handler asks FunctionKeyFocus when a probe sets
    // it, so this runs without a real keyboard.
    static void VerifyFunctionKeyFocus(MainWindow window,Action<string,bool,string> check){
        window.SetText("Body line\n");window.SetMode(true);window.source.CaretIndex=0;LongLineBench.Idle();
        check("keys: input fields own F-keys",FieldOwnsFunctionKey(window.query)&&FieldOwnsFunctionKey(window.sizes)&&FieldOwnsFunctionKey(new ComboBox())&&FieldOwnsFunctionKey(new TextBox())&&FieldOwnsFunctionKey(new ComboBoxItem()),"query="+FieldOwnsFunctionKey(window.query)+", sizeBox="+FieldOwnsFunctionKey(window.sizes)+", comboItem="+FieldOwnsFunctionKey(new ComboBoxItem()));
        check("keys: the editor does not",!FieldOwnsFunctionKey(window.source.TextArea)&&!FieldOwnsFunctionKey(window.source),"textArea="+FieldOwnsFunctionKey(window.source.TextArea)+", editor="+FieldOwnsFunctionKey(window.source));
        string before=window.text;
        try{
            FunctionKeyFocus=delegate{return window.query;};
            var fieldKey=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.F3){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            window.RaiseEvent(fieldKey);LongLineBench.Idle();
            check("keys: F3 with the find box focused stays in the field",!fieldKey.Handled&&window.text==before,"handled="+fieldKey.Handled+", textChanged="+(window.text!=before));
            FunctionKeyFocus=delegate{return window.source.TextArea;};
            var bodyKey=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.F3){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            window.RaiseEvent(bodyKey);LongLineBench.Idle();
            check("keys: F3 on the body still runs the shortcut",bodyKey.Handled&&window.text!=before,"handled="+bodyKey.Handled+", textChanged="+(window.text!=before));
        }finally{FunctionKeyFocus=null;}
    }
    // Word wrap is a saved view setting now (P3-24): the window field, the menu check and the saved
    // snapshot follow the same value.
    static void VerifyWrapPreference(MainWindow window,Action<string,bool,string> check){
        window.SetWrap(false);LongLineBench.Idle();
        check("wrap: off reaches the window, the menu and the snapshot",!window.wordWrap&&window.wrapToggle.IsChecked==false&&!window.WindowPreferences().WordWrap,"wrap="+window.wordWrap+", checked="+window.wrapToggle.IsChecked+", snapshot="+window.WindowPreferences().WordWrap);
        window.SetWrap(true);LongLineBench.Idle();
        check("wrap: on again",window.wordWrap&&window.wrapToggle.IsChecked==true&&window.WindowPreferences().WordWrap,"wrap="+window.wordWrap+", checked="+window.wrapToggle.IsChecked+", snapshot="+window.WindowPreferences().WordWrap);
    }
    internal static int RunSettingsMatrixProbe(string folder) {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var rows=new StringBuilder();bool all=true;int cases=0;
        Action<bool,string,string,string,string,string,string> line=delegate(bool ok,string view,string font,string size,string percent,string check,string detail){
            all&=ok;rows.Append(ok?"PASS ":"FAIL ").Append(view).Append(' ').Append(font).Append(' ').Append(size).Append(' ').Append(percent).Append(' ').Append(check).Append(' ').Append(detail).Append('\n');};
        string fixture=SettingsMatrixProbeHelpers.BuildFixture();
        string canonical=Canonical(fixture);
        MainWindow window=null;
        try{
            VerifyReviewSettings(delegate(string name,bool ok,string detail){line(ok,"review","-","-","-",name,detail);});
            VerifyPreferenceMerge(folder,delegate(string name,bool ok,string detail){line(ok,"prefs","-","-","-",name,detail);});
            foreach(bool sourceMode in new[]{false,true}){
                string view=sourceMode?"source":"live";
                window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=1000,Height=760,Left=-20000,Top=-20000};
                window.Show();window.textMode=false;window.SetText(fixture);window.SetMode(true);
                if(sourceMode){window.loading=true;window.preview.IsChecked=true;window.loading=false;window.SwitchSurface();}
                LongLineBench.Idle();window.UpdateLayout();LongLineBench.Idle();
                line(Canonical(window.text)==canonical,view,"-","-","-","fixture","markdown text equals the fixture, length="+Canonical(window.text).Length);
                string baseline=Canonical(window.text);
                int home=SettingsMatrixProbeHelpers.MidOffset(canonical);
                double[] sizes={8,12,20,48};double[] percents={50,100,200,400};
                string[] fonts={"Malgun Gothic","Consolas","Batang"};
                foreach(string font in fonts){
                    foreach(double size in sizes){
                        foreach(double percent in percents){
                            foreach(bool wrapOff in new[]{false,true}){
                                cases++;
                                string fontNow=font,sizeNow=SettingsMatrixProbeHelpers.N(size),percentNow=SettingsMatrixProbeHelpers.N(percent);
                                Action<string,bool,string> mark=delegate(string check,bool ok,string detail){line(ok,view,fontNow,sizeNow,percentNow,check,detail);};
                                try{
                                    window.source.CaretIndex=Math.Max(0,Math.Min(ActualSourceOffset(window.source.Text,home),window.source.Document.TextLength));
                                    LongLineBench.Idle();window.UpdateLayout();
                                    int caretBefore=Canonical(window.text.Substring(0,window.source.CaretIndex)).Length;
                                    int topBefore=window.source.Document.GetLineByOffset(window.FirstVisibleSourceOffset()).LineNumber;
                                    window.SetBaseFont(font,size);window.SetLinePercent(percent);window.SetWrap(!wrapOff);
                                    LongLineBench.Idle();window.UpdateLayout();LongLineBench.Idle();
                                    int caretAfter=Canonical(window.text.Substring(0,Math.Min(window.source.CaretIndex,window.source.Document.TextLength))).Length;
                                    int topAfter=window.source.Document.GetLineByOffset(window.FirstVisibleSourceOffset()).LineNumber;
                                    mark("text",Canonical(window.text)==baseline,"settings left the markdown exact");
                                    mark("caret",caretAfter==caretBefore,"offset "+caretBefore+" -> "+caretAfter);
                                    mark("reading",Math.Abs(topAfter-topBefore)<=1,"top line "+topBefore+" -> "+topAfter);
                                    bool gutterOk;string gutterDetail=SettingsMatrixProbeHelpers.SourceGutter(window.textGutter,window.source,out gutterOk);
                                    mark("gutter",gutterOk,gutterDetail);
                                    if(window.LiveFormat){window.RefreshSizeBox();double drawn=window.LiveDrawnSize();mark("size box",window.sizes.Text==SettingsMatrixProbeHelpers.N(drawn)+" px","actual='"+window.sizes.Text+"' drawn="+SettingsMatrixProbeHelpers.N(drawn)+" base="+SettingsMatrixProbeHelpers.N(size));}
                                    if(wrapOff){
                                        // The caret is set programmatically to the middle of the document,
                                        // so its line can sit outside the laid-out rows. CalculateCaretRectangle
                                        // still gives its document-space box without scrolling the reading view;
                                        // subtract the horizontal offset to ask whether that box is reachable.
                                        Rect caret=window.source.TextArea.Caret.CalculateCaretRectangle();
                                        double viewRight=window.source.ActualWidth-window.source.Padding.Right-SystemParameters.VerticalScrollBarWidth+1;
                                        bool fits=!caret.IsEmpty&&caret.Right-window.source.HorizontalOffset>=0&&caret.Right<viewRight;
                                        mark("wrap off line reach",fits,"caret right="+SettingsMatrixProbeHelpers.N(caret.Right)+" offset="+SettingsMatrixProbeHelpers.N(window.source.HorizontalOffset));
                                    }
                                    if(SettingsMatrixProbeHelpers.WantsShot("size"+sizeNow+"_"+percentNow+(wrapOff?"_nowrap":""))){
                                        Program.Snapshot(window,Path.Combine(folder,"settings-"+view+"-size"+sizeNow+"-"+percentNow+(wrapOff?"-nowrap":"")+".png"));
                                    }
                                }catch(Exception ex){line(false,view,fontNow,sizeNow,percentNow,"exception",ex.GetType().Name+": "+ex.Message);}
                            }
                        }
                    }
                }
                // Tables and the body font: a settings change must not turn the two fixture tables
                // into plain paragraphs, and every line that had a table still does.
                window.SetBaseFont("Malgun Gothic",16);window.SetLinePercent(150);window.SetWrap(true);
                LongLineBench.Idle();window.UpdateLayout();
                int tables=SettingsMatrixProbeHelpers.TableLines(window.source.Document);
                line(tables==2,view,"-","-","-","tables","table delimiter lines="+tables);
                if(!sourceMode){
                    VerifyFunctionKeyFocus(window,delegate(string name,bool ok,string detail){line(ok,"keys","-","-","-",name,detail);});
                    VerifyWrapPreference(window,delegate(string name,bool ok,string detail){line(ok,"wrap","-","-","-",name,detail);});
                }
                window.CaptureTab();foreach(var state in window.documents)state.Saved=state.Text;
                window.saved=window.text;window.Close();window=null;LongLineBench.Idle();
            }
        }catch(Exception ex){all=false;rows.Append("EXCEPTION ").Append(ex).Append('\n');}
        finally{if(window!=null){try{window.saved=window.text;window.Close();}catch(Exception){}}}
        rows.Append("Cases: ").Append(cases).Append("\nResult: ").Append(all?"PASS":"FAIL").Append('\n');
        try{
            string chosen=Path.Combine(folder,all?"settings-probe-PASS.txt":"settings-probe-FAIL.txt"),other=Path.Combine(folder,all?"settings-probe-FAIL.txt":"settings-probe-PASS.txt");
            File.WriteAllText(Path.Combine(folder,"settings-probe.txt"),rows.ToString(),new UTF8Encoding(false));
            if(File.Exists(other))File.Delete(other);
            File.WriteAllText(chosen,rows.ToString(),new UTF8Encoding(false));
        }catch(IOException){return 1;}
        return all?0:1;
    }
}
internal static class SettingsMatrixProbeHelpers {
    internal static string Canon(string value){return (value??"").Replace("\r\n","\n").Replace('\r','\n');}
    internal static string N(double value){return value.ToString("0.##",CultureInfo.InvariantCulture);}
    internal static string RectText(Rect value){return value.IsEmpty?"empty":"("+N(value.Left)+","+N(value.Top)+")-("+N(value.Right)+","+N(value.Bottom)+")";}
    // The pictures worth keeping: the extreme sizes and a wrap-off combo.
    internal static bool WantsShot(string name){return name=="size48_50"||name=="size48_400_nowrap"||name=="size8_50";}
    internal static bool IsFontInstalled(string name){
        foreach(FontFamily family in Fonts.SystemFontFamilies){
            if(String.Equals(family.Source,name,StringComparison.OrdinalIgnoreCase))return true;
            try{foreach(var key in family.FamilyNames)if(String.Equals(key.Value,name,StringComparison.OrdinalIgnoreCase))return true;}catch(ArgumentException){}
        }
        return false;
    }
    // A caret position in the middle of the document, on a line start.
    internal static int MidOffset(string canonical){
        if(canonical.Length==0)return 0;
        int middle=canonical.Length/2;
        int start=canonical.IndexOf('\n',middle);
        if(start<0)return canonical.Length;
        start++;
        return start>=canonical.Length?middle:start;
    }
    // Table delimiter lines in the source: two fixture tables, five delimiter-like rows (the
    // nested table and the fenced code sample are counted by the same grammar the editor uses).
    internal static int TableLines(ICSharpCode.AvalonEdit.Document.TextDocument document){
        int count=0;int first,last;
        for(int n=1;n<=document.LineCount;n++){
            if(MarkdownTableCommands.FindTable(document,document.GetLineByNumber(n).Offset,out first,out last)){if(n==first+1)count++;}
        }
        return count;
    }
    internal static string BuildFixture(){
        var text=new StringBuilder();
        text.Append("# Settings matrix fixture\n\n");
        text.Append("A generated document for the font and spacing matrix probe. It holds every block\n");
        text.Append("kind the editor draws, so a settings change can be checked against the same body.\n\n");
        text.Append("## 인라인 서식\n\n");
        text.Append("This paragraph has **bold words**, *italic words*, `inline code` and a [link](https://example.com/page).\n");
        text.Append("A second sentence uses **bold and _nested italic_** together so the writer must keep both markers.\n\n");
        text.Append("The word sample appears once here so a find can land on it and a bold command can wrap it.\n\n");
        text.Append("## 긴 문단 (wrapped)\n\n");
        text.Append("This deliberately long paragraph is meant to wrap onto several visual lines at every font size and line spacing the matrix tries. ");
        text.Append("It mixes English prose with 한국어 문장 to exercise both scripts in one run of text, and it keeps going long enough that the paragraph height changes noticeably when the base font or the line spacing changes. ");
        text.Append("Wrapping must stay inside the body column at 8 and at 48 pixels, and the reading position must not jump when the base settings are changed under the reader. ");
        text.Append("The last sentence ends with a short tail so a wrap regression that clips the final word is visible in the rendered picture as well.\n\n");
        text.Append("## 목록\n\n");
        text.Append("- First bullet item\n");
        text.Append("  - Second level bullet\n");
        text.Append("    - Third level bullet\n");
        text.Append("  - Second level again\n");
        text.Append("- Another top level bullet with a longer sentence that has to wrap under its own marker\n");
        text.Append("\n1. First numbered item\n2. Second numbered item\n3. Third numbered item with a longer body so numbering and wrapping meet\n");
        text.Append("\n- [ ] Unchecked task\n- [x] Checked task\n\n");
        text.Append("## 표\n\n");
        text.Append("| Block | Purpose | Rendered at |\n");
        text.Append("| --- | --- | --- |\n");
        text.Append("| heading | level one and two | body width |\n");
        text.Append("| paragraph | wrapped prose | body width |\n");
        text.Append("| table | three columns | content width |\n");
        text.Append("| code | fenced block | padded box |\n\n");
        text.Append("- List item owning a table\n");
        text.Append("  | Name | Count | Note |\n");
        text.Append("  | --- | --- | --- |\n");
        text.Append("  | alpha | 1 | inside a list item |\n");
        text.Append("  | beta | 2 | second row |\n\n");
        text.Append("## 코드 블록\n\n");
        text.Append("```text\n");
        text.Append("static void Main() {\n");
        text.Append("    Console.WriteLine(\"code block body\");\n");
        text.Append("}\n");
        text.Append("```\n\n");
        text.Append("## 인용문\n\n");
        text.Append("> A quoted paragraph keeps its bar and its muted ink when the base font changes.\n");
        text.Append("> A second quoted line stays inside the same quote block.\n\n");
        text.Append("## 한 줄 긴 줄\n\n");
        text.Append(BuildLongLine());
        text.Append("\n\n");
        for(int index=0;index<60;index++){
            text.Append("### Section ").Append(index+1).Append("\n\n");
            text.Append("Body paragraph ").Append(index+1).Append(" carries plain prose for the matrix. ");
            text.Append(index%3==0?"It also has **bold** text. ":index%3==1?"It also has *italic* text. ":"It also has `code` text. ");
            text.Append("한국어 문장도 함께 넣어 두 스크립트의 줄 높이를 같이 확인합니다.\n\n");
            if(index%10==9)text.Append("- marker bullet ").Append(index+1).Append("\n\n");
        }
        text.Append("## 끝 문단\n\n");
        text.Append("The final paragraph closes the generated fixture.\n");
        return text.ToString();
    }
    // One 600-character line: short enough to stay a single visual line with wrap off and long
    // enough that the horizontal scroll range must grow.
    static string BuildLongLine(){
        var line=new StringBuilder();
        int word=0;
        while(line.Length<600){if(line.Length>0)line.Append(' ');line.Append("word").Append((word++%97)+1);}
        return line.ToString(0,600);
    }
    // The placement rule, recomputed from geometry and font metrics rather than by calling the
    // gutter's own function: a row raised by a larger run starts at the virtual body baseline
    // (row top + half the body row's extra line spacing + the body ascent); every other row keeps
    // the top it always had. Tolerance is 1px for the virtual baseline, 0.5px for the old one.
    internal static double VirtualTopExpected(double rowTop,double rowBottom,double lineHeight,FormattedText body,double labelBaseline,double labelHeight){
        double spacing=Double.IsNaN(lineHeight)?0:lineHeight-body.Height;
        if(spacing<0)spacing=0;
        double top=rowTop+spacing/2+body.Baseline-labelBaseline;
        if(top<rowTop||top+labelHeight>rowBottom)top=rowTop;
        return top;
    }
    internal static FormattedText BodyGlyph(FontFamily family,FontStyle style,FontWeight weight,FontStretch stretch,double size,double dpi){
        return new FormattedText("Mg",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(family,style,weight,stretch),size,Brushes.Black,dpi);
    }
    // A heading, a paragraph whose FIRST run is 40px, and a paragraph whose big run comes after
    // body text: three shapes where the line's baseline belongs to a run larger than the body.
    internal static string NumberTopFixture(){
        var text=new StringBuilder();
        text.Append("# Large heading line\n\n");
        text.Append("<span style=\"font-size:40px\">Big</span> body text after the large run\n\n");
        text.Append("Body text before the <span style=\"font-size:40px\">big</span> run\n\n");
        text.Append("An ordinary body line with no large run at all\n\n");
        text.Append("Another ordinary body line underneath it\n");
        return text.ToString();
    }
    // Source view. The check reads what the gutter actually painted last (TextGutter.Painted):
    // each number's box must sit inside its own row, stay in the column left of the body, and the
    // numbers must not draw into the row above.
    internal static string SourceGutter(TextGutter gutter,SourceEditor editor,out bool ok){
        ok=true;
        if(editor==null)return "no editor";
        if(gutter.Visibility!=Visibility.Visible)return "gutter hidden (read mode)";
        var view=editor.TextArea.TextView;
        if(view==null)return "no text view";
        view.EnsureVisualLines();
        if(view.VisualLines.Count==0)return "no visual lines";
        double dpi=VisualTreeHelper.GetDpi(gutter).PixelsPerDip;
        double columnRight=gutter.ActualWidth,bodyLeft=editor.Margin.Left+editor.Padding.Left;
        int rows=0,offCentre=0,pastBody=0,overlap=0,misaligned=0,missing=0;double previousBottom=Double.NaN;string worst="";
        var drawn=new HashSet<int>();
        GutterPainted painted=gutter.Painted;
        for(int index=0;index<painted.Count;index++){
            GutterNumber number=painted[index];
            Rect rect=editor.GetRectFromCharacterIndex(editor.Document.GetLineByNumber(number.Line).Offset);
            if(rect.IsEmpty||rect.Bottom<0||rect.Top>=gutter.ActualHeight)continue;
            rows++;drawn.Add(number.Line);
            var visual=view.GetVisualLine(number.Line);
            if(visual==null||visual.TextLines.Count==0)continue;
            double want,tolerance;
            if(SourceRowRaised(editor,visual)){
                double rowTop=view.TranslatePoint(new Point(0,visual.GetTextLineVisualYPosition(visual.TextLines[0],ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop)-view.VerticalOffset),gutter).Y;
                want=VirtualTopExpected(rowTop,rowTop+visual.TextLines[0].Height,TextBlock.GetLineHeight(editor),BodyGlyph(editor.FontFamily,editor.FontStyle,editor.FontWeight,editor.FontStretch,editor.FontSize,dpi),number.Baseline-number.Top,number.Height);
                tolerance=1;
            } else {
                double baseline=view.TranslatePoint(new Point(0,visual.GetTextLineVisualYPosition(visual.TextLines[0],ICSharpCode.AvalonEdit.Rendering.VisualYPosition.Baseline)-view.VerticalOffset),gutter).Y;
                want=baseline-(number.Baseline-number.Top);
                tolerance=.5;
            }
            if(Double.IsNaN(want)||Math.Abs(number.Top-want)>tolerance){misaligned++;if(worst.Length==0)worst=" align="+N(want)+" got="+N(number.Top)+" line="+number.Line;}
            if(number.Height>number.RowHeight+1){offCentre++;if(worst.Length==0)worst=" first="+number.Line+" label="+N(number.Height)+" row="+N(number.RowHeight);}
            if(number.Left+number.Width>bodyLeft+1||number.Left<0)pastBody++;
            if(!Double.IsNaN(previousBottom)&&number.Top<previousBottom-1){overlap++;if(worst.Length==0)worst=" first="+number.Line+" step="+N(number.Top-previousBottom);}
            previousBottom=number.Top+number.Height;
        }
        foreach(var visual in view.VisualLines){Rect rect=editor.GetRectFromCharacterIndex(visual.FirstDocumentLine.Offset);if(!rect.IsEmpty&&rect.Bottom>0&&rect.Top<gutter.ActualHeight&&!drawn.Contains(visual.FirstDocumentLine.LineNumber))missing++;}
        ok=rows>0&&missing==0&&misaligned==0&&offCentre==0&&pastBody==0&&overlap==0&&NumberInk(gutter)>0;
        return "rows="+rows+"/"+painted.Count+" missing="+missing+" alignFail="+misaligned+" tall="+offCentre+" pastBody="+pastBody+" overlapping="+overlap
            +" column="+N(columnRight)+" bodyLeft="+N(bodyLeft)+worst;
    }
    // True when the first physical row's own text carries a run larger than the editor's body font.
    static bool SourceRowRaised(SourceEditor editor,ICSharpCode.AvalonEdit.Rendering.VisualLine visual){
        if(visual==null||visual.TextLines.Count==0)return false;
        int firstRowLength=visual.TextLines[0].Length;
        foreach(var element in visual.Elements){
            if(element==null||element.VisualColumn>=firstRowLength)continue;
            var properties=element.TextRunProperties;
            if(properties!=null&&properties.FontRenderingEmSize>editor.FontSize+.5)return true;
        }
        return false;
    }
    // Count alpha-bearing label pixels only, not transparent black or the column's border.
    static int NumberInk(FrameworkElement gutter){
        int width=(int)Math.Ceiling(gutter.ActualWidth),height=(int)Math.Ceiling(gutter.ActualHeight);if(width<6||height<1)return 0;
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(gutter);
        var pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);int ink=0;
        for(int y=0;y<height;y++)for(int x=2;x<width-3;x++)if(pixels[(y*width+x)*4+3]>30)ink++;
        return ink;
    }
}
}
