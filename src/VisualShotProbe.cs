using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MDNote {
// --visual-shot-probe <dir>: real-window screenshots for human review: help/example documents in every
// UI language, top menus and toolbar dropdowns in both themes, the narrow overflow menu, and a table
// fixture in basic/live mode. Every shot is written at 100% (96 dpi) and at 150% (144 dpi, "-150").
internal static class VisualShotProbe {
    internal static int Run(string dir) {
        return MainWindow.RunVisualShots(dir);
    }
}
public partial class MainWindow {
    sealed class VisualShotPopup {
        internal string Label;
        internal FrameworkElement Visual;
        internal FrameworkElement AnchorVisual;
        internal Rect Bounds;
        internal Rect Anchor;
        internal bool ExpectedDark;
        internal bool BelowAnchor;
        internal bool Manual;
        internal double Tolerance;
    }
    sealed class VisualShotEntry {
        internal string Group;
        internal string File;
        internal string Description;
    }
    static readonly string[] VisualShotCodes={"ko","en","ja","zh-CN","zh-TW"};
    static readonly double[] VisualShotScales={1.0,1.5};
    static readonly string[] VisualShotMenuKeys={"file","edit","view","shortcuts","language","help"};
    static readonly string[] VisualShotMenuHeaders={"파일(_F)","편집(_E)","보기(_V)","단축키","언어","도움말(_H)"};
    static List<string> VisualShotFindings;

    internal static int RunVisualShots(string dir){
        dir=Path.GetFullPath(dir);Directory.CreateDirectory(dir);
        string passFile=Path.Combine(dir,"visual-shot-probe-PASS.txt");
        string failFile=Path.Combine(dir,"visual-shot-probe-FAIL.txt");
        try{if(File.Exists(passFile))File.Delete(passFile);}catch(Exception){}
        try{if(File.Exists(failFile))File.Delete(failFile);}catch(Exception){}
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        bool oldHide=hideMarkers;
        var entries=new List<VisualShotEntry>();
        var failures=new List<string>();
        var checks=new StringBuilder();
        VisualShotFindings=new List<string>();
        string baseDir=AppDomain.CurrentDomain.BaseDirectory;
        string fixture=Path.Combine(dir,"visual-table.md");
        try{
            File.WriteAllText(fixture,VisualShotTableFixture(),new UTF8Encoding(false));
            VisualShotHelpShots(dir,baseDir,checks,failures,entries);
            VisualShotMenuShots(dir,fixture,false,checks,failures,entries);
            VisualShotMenuShots(dir,fixture,true,checks,failures,entries);
            VisualShotShortcutShots(dir,fixture,false,checks,failures,entries);
            VisualShotShortcutShots(dir,fixture,true,checks,failures,entries);
            VisualShotToolbarShots(dir,fixture,false,checks,failures,entries);
            VisualShotToolbarShots(dir,fixture,true,checks,failures,entries);
            VisualShotOverflowShots(dir,fixture,false,checks,failures,entries);
            VisualShotOverflowShots(dir,fixture,true,checks,failures,entries);
            VisualShotTableShots(dir,fixture,checks,failures,entries);
        }catch(Exception error){
            failures.Add("probe exception: "+error.GetType().Name+": "+error.Message+" @ "+VisualShotOneLine(error.StackTrace));
        }finally{
            hideMarkers=oldHide;
        }
        var text=new StringBuilder();
        text.AppendLine("visual-shot-probe");
        text.AppendLine("dir: "+dir);
        text.AppendLine("run: "+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture));
        text.AppendLine("scales: 100% (96 dpi) and 150% (144 dpi, file suffix -150)");
        text.AppendLine("window: "+(int)VisualShotWidth()+"x"+(int)VisualShotHeight()+" logical px; captures the window content area");
        text.AppendLine();
        text.AppendLine("PNG count by group");
        var groups=new List<string>();var counts=new Dictionary<string,int>();
        foreach(var entry in entries){
            if(!counts.ContainsKey(entry.Group)){groups.Add(entry.Group);counts[entry.Group]=0;}
            counts[entry.Group]=counts[entry.Group]+1;
        }
        foreach(string group in groups)text.AppendLine("  "+group+": "+counts[group]);
        text.AppendLine("  total: "+entries.Count);
        text.AppendLine();
        foreach(string group in groups){
            text.AppendLine("["+group+"]");
            foreach(var entry in entries){
                if(entry.Group!=group)continue;
                text.AppendLine("  "+entry.File+" : "+entry.Description);
            }
            text.AppendLine();
        }
        text.AppendLine("Automatic checks");
        text.Append(checks.ToString());
        text.AppendLine();
        text.AppendLine("Product findings (visual defects visible in the PNGs; not capture failures)");
        if(VisualShotFindings.Count==0)text.AppendLine("  none");
        else foreach(string finding in VisualShotFindings)text.AppendLine("  - "+finding);
        text.AppendLine();
        text.AppendLine("Failures ("+failures.Count+")");
        foreach(string failure in failures)text.AppendLine("  - "+failure);
        text.AppendLine();
        text.AppendLine("RESULT: "+(failures.Count==0?"PASS":"FAIL"));
        File.WriteAllText(Path.Combine(dir,"visual-shot-probe.txt"),text.ToString(),new UTF8Encoding(false));
        if(failures.Count==0)File.WriteAllText(passFile,"PASS\n",new UTF8Encoding(false));
        else{
            var failed=new StringBuilder();failed.AppendLine("FAIL "+failures.Count);
            foreach(string failure in failures)failed.AppendLine(failure);
            File.WriteAllText(failFile,failed.ToString(),new UTF8Encoding(false));
        }
        return failures.Count==0?0:1;
    }

    static string VisualShotTableFixture(){
        var text=new StringBuilder();
        text.Append("# 표 확인\n\n");
        text.Append("| 왼쪽 정렬 | 가운데 정렬 | 오른쪽 정렬 |\n");
        text.Append("|:---|:---:|---:|\n");
        text.Append("| 짧음 | 보통 | 1,200 |\n");
        text.Append("| 둘째 줄<br>셋째 줄 | 아주 길고 긴 한국어 셀 내용을 넣어 자동 줄바꿈과 열 너비가 어떻게 처리되는지 확인합니다 | 42 |\n");
        text.Append("| 파이프 \\| 문자 | **굵게** `코드` | 3.50 |\n\n");
        text.Append("| 항목 | 값 | 비고 |\n");
        text.Append("|:---:|:---:|---:|\n");
        text.Append("| 사과 | 1,200 | 빨강 |\n");
        text.Append("| 바나나 | 300 | 노랑 |\n\n");
        text.Append("- 목록 항목 하나\n");
        text.Append("- 목록 항목 둘\n\n");
        text.Append("```text\n");
        text.Append("코드 블록 안입니다.\n");
        text.Append("```\n\n");
        text.Append("표 아래 문단입니다.\n");
        return text.ToString();
    }

    static void VisualShotHelpShots(string dir,string baseDir,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        foreach(string code in VisualShotCodes){
            string help=code=="ko"?"MDNote 사용법.md":"MDNote-guide."+code+".md";
            string example=code=="ko"?"마크다운 작성 예제.md":"example."+code+".md";
            VisualShotHelpShot(dir,baseDir,code,help,"help","top",false,checks,failures,entries);
            VisualShotHelpShot(dir,baseDir,code,help,"help","mid",true,checks,failures,entries);
            VisualShotHelpShot(dir,baseDir,code,example,"example","top",false,checks,failures,entries);
        }
    }

    static void VisualShotHelpShot(string dir,string baseDir,string code,string file,string kind,string position,bool scrolled,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        string path=Path.Combine(baseDir,file);
        string baseName=kind+"-"+code+"-"+position;
        if(!File.Exists(path)){failures.Add(baseName+": guide file is missing next to the exe: "+path);return;}
        MainWindow window=null;
        try{
            window=VisualShotNewWindow(false,1080,800);
            window.ToolbarLanguage(code);
            window.LoadFileCore(path);
            VisualShotSettle(window);
            if(window.editing)window.SetMode(false);
            VisualShotSettle(window);
            int chars=window.text.Length;
            int lines=window.source.Document.LineCount;
            if(chars<300)failures.Add(baseName+": document did not load (chars="+chars+")");
            if(window.source.Document.TextLength<300)failures.Add(baseName+": editor buffer is empty");
            string scrollState="top";
            int tableLine=0;
            if(scrolled){
                tableLine=VisualShotFindAnchorLine(window.source.Document);
                if(tableLine>0){
                    window.source.ScrollToLine(tableLine);VisualShotSettle(window);
                    window.source.ScrollToLine(tableLine);VisualShotSettle(window);
                }
                double offset=window.source.VerticalOffset;
                bool visible=false;
                if(tableLine>0&&window.source.TextArea.TextView!=null){
                    var view=window.source.TextArea.TextView;
                    double top=view.GetVisualTopByDocumentLine(tableLine);
                    double next=tableLine<window.source.Document.LineCount?view.GetVisualTopByDocumentLine(tableLine+1):top+window.source.FontSize;
                    visible=next>offset&&top<offset+window.source.ViewportHeight;
                }
                scrollState="scrolled to line "+tableLine+" offset="+VisualShotNum(offset)+" anchorVisible="+visible;
                if(offset<1)failures.Add(baseName+": scrolled shot did not scroll (offset="+VisualShotNum(offset)+")");
                if(tableLine<=0)failures.Add(baseName+": no table/list/code anchor line found");
                else if(!visible)failures.Add(baseName+": anchor line "+tableLine+" is not visible after scrolling");
            }
            checks.AppendLine(baseName+": document="+file+" lang="+code+" chars="+chars+" lines="+lines+" mode=read theme=light "+scrollState);
            foreach(double scale in VisualShotScales){
                string name=baseName+(scale==1.0?"":"-150");
                string description=(kind=="help"?"Help document ":"Example document ")+file+" ("+code+" UI), "+scrollState+", read mode, light theme";
                VisualShotShoot(window,dir,name,scale,null,description,"group1 help and example documents",false,checks,failures,entries);
            }
        }catch(Exception error){
            failures.Add(baseName+": "+VisualShotError(error));
        }finally{
            if(window!=null)window.Close();
        }
    }

    static void VisualShotMenuShots(string dir,string fixture,bool dark,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        string theme=dark?"dark":"light";
        MainWindow window=null;
        try{
            window=VisualShotNewWindow(true,VisualShotWidth(),VisualShotHeight());
            window.SetDark(dark);VisualShotSettle(window);
            window.LoadFileCore(fixture);VisualShotSettle(window);
            if(window.editing)window.SetMode(false);
            VisualShotSettle(window);
            bool activated=window.Activate();
            VisualShotPump(400);
            checks.AppendLine(theme+"-menu: window activation requested="+activated+" IsActive="+window.IsActive);
            if(window.topMenu.Items.Count<VisualShotMenuKeys.Length)failures.Add(theme+"-menu: menu bar has only "+window.topMenu.Items.Count+" items");
            int count=Math.Min(VisualShotMenuKeys.Length,window.topMenu.Items.Count);
            for(int index=0;index<count;index++){
                var item=window.topMenu.Items[index] as MenuItem;
                if(item==null)continue;
                string key=VisualShotMenuKeys[index];
                string baseName=theme+"-menu-"+key;
                string header=Convert.ToString(item.Header);
                if(header!=VisualShotMenuHeaders[index])checks.AppendLine(baseName+": header ["+header+"] differs from the Korean default ["+VisualShotMenuHeaders[index]+"]");
                if(item.Items.Count==0){
                    checks.AppendLine(baseName+": ["+header+"] has no dropdown; it opens the shortcut settings dialog ("+theme+"-menu-shortcuts.png)");
                    continue;
                }
                var popups=new List<VisualShotPopup>();
                Popup activePopup=null;
                bool manualLayout=false;
                int openedEvents=0,closedEvents=0;
                RoutedEventHandler openedHandler=delegate{openedEvents++;};
                RoutedEventHandler closedHandler=delegate{closedEvents++;};
                item.SubmenuOpened+=openedHandler;
                item.SubmenuClosed+=closedHandler;
                try{
                    try{window.topMenu.Focus();Keyboard.Focus(item);}catch(Exception){}
                    VisualShotPump(150);
                    item.IsSubmenuOpen=true;
                    VisualShotPump(450);
                    item.ApplyTemplate();
                    var popup=item.Template==null?null:item.Template.FindName("PART_Popup",item) as Popup;
                    activePopup=popup;
                    string diag=VisualShotMenuState(window,item,popup)+" focusElement="+(Keyboard.FocusedElement==null?"null":Keyboard.FocusedElement.GetType().Name)+" events=opened:"+openedEvents+"/closed:"+closedEvents;
                    if(popup!=null&&!popup.IsOpen&&!item.IsSubmenuOpen){
                        bool internalOpen=VisualShotOpenMenuInternal(item);
                        VisualShotPump(350);
                        item.ApplyTemplate();
                        popup=item.Template==null?null:item.Template.FindName("PART_Popup",item) as Popup;
                        activePopup=popup;
                        diag+=" | open-menu("+internalOpen+"):"+VisualShotMenuState(window,item,popup)+" events=opened:"+openedEvents+"/closed:"+closedEvents;
                    }
                    if(popup!=null&&!popup.IsOpen&&!item.IsSubmenuOpen){
                        bool clicked=VisualShotInvokeMenuClick(item);
                        VisualShotPump(450);
                        item.ApplyTemplate();
                        popup=item.Template==null?null:item.Template.FindName("PART_Popup",item) as Popup;
                        activePopup=popup;
                        diag+=" | click-path("+clicked+"):"+VisualShotMenuState(window,item,popup)+" events=opened:"+openedEvents+"/closed:"+closedEvents;
                    }
                    if(popup!=null&&!popup.IsOpen){
                        try{popup.IsOpen=true;}catch(Exception openError){diag+=" direct-open-error="+openError.GetType().Name+":"+VisualShotOneLine(openError.Message);}
                        VisualShotPump(450);
                        window.UpdateLayout();
                        diag+=" | direct-popup:"+VisualShotMenuState(window,item,popup)+" events=opened:"+openedEvents+"/closed:"+closedEvents;
                    }
                    if(popup!=null&&item.Role==MenuItemRole.TopLevelHeader){
                        bool bottom=popup.Placement==PlacementMode.Bottom;
                        checks.AppendLine(baseName+": top-menu popup placement="+popup.Placement+(bottom?" PASS":" FAIL"));
                        if(!bottom)failures.Add(baseName+": top-menu popup placement is "+popup.Placement+", expected Bottom");
                    }
                    if(popup==null||!popup.IsOpen){
                        var manual=popup==null?null:popup.Child as FrameworkElement;
                        if(manual!=null)VisualShotEnsureLayout(manual);
                        if(manual==null||manual.ActualWidth<2||manual.ActualHeight<2){failures.Add(baseName+": dropdown did not open ("+diag+")");continue;}
                        var content=(FrameworkElement)window.Content;
                        Rect anchor=VisualShotRectInContent(item,content);
                        Rect manualBounds=new Rect(anchor.Left,anchor.Bottom,manual.ActualWidth,manual.ActualHeight);
                        var manualSpec=new VisualShotPopup{Label=baseName,Visual=manual,AnchorVisual=item,Bounds=manualBounds,Anchor=anchor,ExpectedDark=dark,BelowAnchor=false,Manual=true,Tolerance=dark?2:8};
                        popups.Add(manualSpec);
                        manualLayout=true;
                        checks.AppendLine(baseName+": system popup did not stay open on the hidden desktop; the real menu content was measured by the probe at the template's bottom placement ("+VisualShotNum(manual.ActualWidth)+"x"+VisualShotNum(manual.ActualHeight)+")");
                        VisualShotCheckPopup(window,manualSpec,content,checks,failures);
                    }else{
                        checks.AppendLine(baseName+": opened ("+diag+")");
                        var child=popup.Child as FrameworkElement;
                        if(child==null){failures.Add(baseName+": dropdown has no child");continue;}
                        VisualShotPump(200);
                        VisualShotEnsureLayout(child);
                        var content=(FrameworkElement)window.Content;
                        Rect anchor=VisualShotRectInContent(item,content);
                        Rect bounds=VisualShotBoundsInContent(child,content);
                        if(bounds.IsEmpty||bounds.Width<2||bounds.Height<2)bounds=new Rect(anchor.Left,anchor.Bottom,Math.Max(1,child.ActualWidth),Math.Max(1,child.ActualHeight));
                        var spec=new VisualShotPopup{Label=baseName,Visual=child,AnchorVisual=item,Bounds=bounds,Anchor=anchor,ExpectedDark=dark,BelowAnchor=true,Tolerance=dark?2:8};
                        popups.Add(spec);
                        VisualShotCheckPopup(window,spec,content,checks,failures);
                    }
                }catch(Exception error){
                    failures.Add(baseName+": "+VisualShotError(error));
                }finally{
                    item.SubmenuOpened-=openedHandler;
                    item.SubmenuClosed-=closedHandler;
                }
                try{
                    foreach(double scale in VisualShotScales){
                        string name=baseName+(scale==1.0?"":"-150");
                        string description="Main window, "+theme+" theme, top menu ["+header+"] opened, items="+item.Items.Count+(manualLayout?" (real menu content laid out by the probe; the system popup closes on the hidden desktop)":"");
                        VisualShotShoot(window,dir,name,scale,popups,description,"group2 top menus",dark,checks,failures,entries);
                    }
                }finally{
                    if(activePopup!=null){try{activePopup.IsOpen=false;}catch(Exception){}}
                    try{item.IsSubmenuOpen=false;VisualShotPump(250);}catch(Exception){}
                }
            }
        }catch(Exception error){
            failures.Add(theme+"-menu: "+VisualShotError(error));
        }finally{
            if(window!=null)window.Close();
        }
    }

    static void VisualShotShortcutShots(string dir,string fixture,bool dark,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        string theme=dark?"dark":"light";
        string baseName=theme+"-menu-shortcuts";
        MainWindow window=null;
        try{
            window=VisualShotNewWindow(true,VisualShotWidth(),VisualShotHeight());
            window.SetDark(dark);VisualShotSettle(window);
            window.LoadFileCore(fixture);VisualShotSettle(window);
            if(window.editing)window.SetMode(false);
            VisualShotSettle(window);
            bool saw=false;int ticks=0;int captured=0;
            var timer=new DispatcherTimer(DispatcherPriority.Background);
            timer.Interval=TimeSpan.FromMilliseconds(150);
            timer.Tick+=delegate{
                ticks++;
                Window dialog=VisualShotOwnedDialog(window);
                if(dialog==null){if(ticks>40)timer.Stop();return;}
                saw=true;
                try{
                    VisualShotSettle(dialog);
                    foreach(double scale in VisualShotScales){
                        string name=baseName+(scale==1.0?"":"-150");
                        string description="F1-F12 shortcut settings dialog, "+theme+" theme, opened from the 단축키 top menu";
                        VisualShotShoot(dialog,dir,name,scale,null,description,"group2 shortcut settings dialog",false,checks,failures,entries);
                        captured++;
                    }
                }catch(Exception error){
                    failures.Add(baseName+": "+VisualShotError(error));
                }
                timer.Stop();
                try{dialog.Close();}catch(Exception){}
            };
            timer.Start();
            try{window.ShowShortcutSettings();}finally{timer.Stop();}
            if(!saw)failures.Add(baseName+": the shortcut settings dialog did not appear");
            else checks.AppendLine(baseName+": dialog captured at "+captured+" scale(s)");
        }catch(Exception error){
            failures.Add(baseName+": "+VisualShotError(error));
        }finally{
            if(window!=null)window.Close();
        }
    }

    static void VisualShotOverflowShots(string dir,string fixture,bool dark,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        string theme=dark?"dark":"light";
        MainWindow window=null;
        try{
            window=VisualShotNewWindow(true,460,VisualShotHeight());
            window.SetDark(dark);VisualShotSettle(window);
            window.LoadFileCore(fixture);VisualShotSettle(window);
            window.SetMode(true);VisualShotSettle(window);
            window.SetLiveMode(false);VisualShotSettle(window);
            window.ToolbarRefreshOverflow();VisualShotPump(500);window.UpdateLayout();
            window.ToolbarRefreshOverflow();VisualShotPump(300);VisualShotSettle(window);
            var content=(FrameworkElement)window.Content;
            checks.AppendLine(theme+"-overflow: narrow window content="+VisualShotNum(content.ActualWidth)+"x"+VisualShotNum(content.ActualHeight));
            if(!window.ToolbarOverflowShown(false))failures.Add(theme+"-overflow: top-row » button is not shown at 460 px");
            if(!window.ToolbarOverflowShown(true))failures.Add(theme+"-overflow: format-ribbon » button is not shown at 460 px");
            VisualShotOverflowOne(window,dir,theme,true,dark,content,checks,failures,entries);
            VisualShotOverflowOne(window,dir,theme,false,dark,content,checks,failures,entries);
        }catch(Exception error){
            failures.Add(theme+"-overflow: "+VisualShotError(error));
        }finally{
            if(window!=null)window.Close();
        }
    }

    static void VisualShotOverflowOne(MainWindow window,string dir,string theme,bool ribbon,bool dark,FrameworkElement content,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        string name=theme+"-overflow-"+(ribbon?"ribbon":"top");
        Button button=ribbon?window.ribbonOverflow:window.topOverflow;
        ContextMenu menu=ribbon?window.ribbonOverflowMenu:window.topOverflowMenu;
        if(button==null||menu==null){failures.Add(name+": overflow button or menu is missing");return;}
        var popups=new List<VisualShotPopup>();
        try{
            window.OpenOverflowMenu(button,menu);
            VisualShotPump(500);window.UpdateLayout();VisualShotPump(200);
            if(!menu.IsOpen){failures.Add(name+": » menu did not open");return;}
            VisualShotEnsureLayout(menu);
            Rect anchor=VisualShotRectInContent(button,content);
            double w=Math.Max(1,menu.ActualWidth),h=Math.Max(1,menu.ActualHeight);
            double x=anchor.Right-w;
            if(x<0)x=0;
            if(x+w>content.ActualWidth)x=Math.Max(0,content.ActualWidth-w);
            Rect fallback=new Rect(x,anchor.Bottom,w,h);
            Rect bounds=VisualShotBoundsInContent(menu,content);
            if(bounds.IsEmpty||bounds.Width<2||bounds.Height<2)bounds=fallback;
            var spec=new VisualShotPopup{Label=name,Visual=menu,AnchorVisual=button,Bounds=bounds,Anchor=anchor,ExpectedDark=dark,BelowAnchor=false,Tolerance=3};
            popups.Add(spec);
            VisualShotCheckPopup(window,spec,content,checks,failures);
            foreach(double scale in VisualShotScales){
                string file=name+(scale==1.0?"":"-150");
                string description="Narrow 460 px window, "+theme+" theme, "+(ribbon?"format-ribbon":"top-row")+" » overflow menu opened";
                VisualShotShoot(window,dir,file,scale,popups,description,"group2 narrow overflow menu",dark,checks,failures,entries);
            }
        }catch(Exception error){
            failures.Add(name+": "+VisualShotError(error));
        }finally{
            try{menu.IsOpen=false;VisualShotPump(250);}catch(Exception){}
        }
    }

    static void VisualShotToolbarShots(string dir,string fixture,bool dark,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        string theme=dark?"dark":"light";
        MainWindow window=null;
        try{
            window=VisualShotNewWindow(true,VisualShotWidth(),VisualShotHeight());
            window.SetDark(dark);VisualShotSettle(window);
            window.LoadFileCore(fixture);VisualShotSettle(window);
            window.SetMode(true);VisualShotSettle(window);
            window.SetLiveMode(false);VisualShotSettle(window);
            try{
                window.headingChevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VisualShotPump(450);
                ContextMenu menu=window.headingMenu;
                if(menu==null||!menu.IsOpen)failures.Add(theme+"-toolbar-heading: menu did not open");
                else{
                    VisualShotToolbarPopupShot(window,dir,theme+"-toolbar-heading","heading dropdown (본문 / 제목 1-6)",menu,window.headingHover,dark,checks,failures,entries);
                    menu.IsOpen=false;VisualShotPump(250);
                }
            }catch(Exception error){failures.Add(theme+"-toolbar-heading: "+VisualShotError(error));}
            try{
                window.listChevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VisualShotPump(450);
                ContextMenu menu=VisualShotListMenu(window);
                if(menu==null||!menu.IsOpen)failures.Add(theme+"-toolbar-list: menu did not open");
                else{
                    VisualShotToolbarPopupShot(window,dir,theme+"-toolbar-list","list dropdown (글머리 기호 / 번호 목록)",menu,window.listHover,dark,checks,failures,entries);
                    menu.IsOpen=false;VisualShotPump(250);
                }
            }catch(Exception error){failures.Add(theme+"-toolbar-list: "+VisualShotError(error));}
            try{
                window.sizes.IsDropDownOpen=true;
                VisualShotPump(450);
                var popup=window.sizes.Template==null?null:window.sizes.Template.FindName("PART_Popup",window.sizes) as Popup;
                var child=popup==null?null:popup.Child as FrameworkElement;
                if(popup==null||!popup.IsOpen||child==null)failures.Add(theme+"-toolbar-size: size dropdown did not open");
                else if(window.sizes.IsEnabled){
                    VisualShotToolbarPopupShot(window,dir,theme+"-toolbar-size","text size dropdown ("+window.sizes.Text+")",child,window.sizes,dark,checks,failures,entries);
                    window.sizes.IsDropDownOpen=false;VisualShotPump(250);
                }else failures.Add(theme+"-toolbar-size: size box is disabled");
            }catch(Exception error){failures.Add(theme+"-toolbar-size: "+VisualShotError(error));}
            try{
                window.colorArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VisualShotPump(450);
                var child=window.colorPopup.Child as FrameworkElement;
                if(!window.colorPopup.IsOpen||child==null)failures.Add(theme+"-toolbar-color: colour popup did not open");
                else{
                    VisualShotToolbarPopupShot(window,dir,theme+"-toolbar-color","text colour palette",child,window.colorMain,dark,checks,failures,entries);
                    window.colorPopup.IsOpen=false;VisualShotPump(250);
                }
            }catch(Exception error){failures.Add(theme+"-toolbar-color: "+VisualShotError(error));}
            try{
                window.tableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VisualShotPump(450);
                ContextMenu menu=window.tablePickerMenu;
                if(menu==null||!menu.IsOpen)failures.Add(theme+"-toolbar-table: table grid picker did not open");
                else{
                    VisualShotToolbarPopupShot(window,dir,theme+"-toolbar-table","table grid picker (8x10)",menu,window.tableButton,dark,checks,failures,entries);
                    menu.IsOpen=false;VisualShotPump(250);
                }
            }catch(Exception error){failures.Add(theme+"-toolbar-table: "+VisualShotError(error));}
        }catch(Exception error){
            failures.Add(theme+"-toolbar: "+VisualShotError(error));
        }finally{
            if(window!=null)window.Close();
        }
    }

    static void VisualShotToolbarPopupShot(MainWindow window,string dir,string name,string what,FrameworkElement visual,FrameworkElement anchor,bool dark,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        string theme=dark?"dark":"light";
        var popups=new List<VisualShotPopup>();
        try{
            VisualShotEnsureLayout(visual);
            var content=(FrameworkElement)window.Content;
            Rect anchorRect=VisualShotRectInContent(anchor,content);
            double w=Math.Max(1,visual.ActualWidth),h=Math.Max(1,visual.ActualHeight);
            Rect fallback=new Rect(anchorRect.Left,anchorRect.Bottom,w,h);
            Rect bounds=VisualShotBoundsInContent(visual,content);
            if(bounds.IsEmpty||bounds.Width<2||bounds.Height<2)bounds=fallback;
            var spec=new VisualShotPopup{Label=name,Visual=visual,AnchorVisual=anchor,Bounds=bounds,Anchor=anchorRect,ExpectedDark=dark,BelowAnchor=true,Tolerance=dark?2:8};
            popups.Add(spec);
            VisualShotCheckPopup(window,spec,content,checks,failures);
            foreach(double scale in VisualShotScales){
                string file=name+(scale==1.0?"":"-150");
                string description="Edit-mode toolbar dropdown: "+what+", "+theme+" theme";
                VisualShotShoot(window,dir,file,scale,popups,description,"group2 toolbar dropdowns",dark,checks,failures,entries);
            }
        }catch(Exception error){
            failures.Add(name+": "+VisualShotError(error));
        }
    }

    static ContextMenu VisualShotListMenu(MainWindow window){
        try{
            if(window.listBulletMenuItem!=null){
                var owner=ItemsControl.ItemsControlFromItemContainer(window.listBulletMenuItem) as ContextMenu;
                if(owner!=null)return owner;
            }
        }catch(Exception){}
        try{
            DependencyObject node=window.listBulletMenuItem;
            for(int i=0;i<40&&node!=null;i++){
                var menu=node as ContextMenu;
                if(menu!=null)return menu;
                node=VisualTreeHelper.GetParent(node);
            }
        }catch(Exception){}
        return null;
    }

    static void VisualShotTableShots(string dir,string fixture,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        foreach(bool live in new bool[]{false,true}){
            foreach(bool inside in new bool[]{false,true}){
                string mode=live?"live":"basic";
                string state=inside?"inside":"outside";
                string baseName="table-"+mode+"-"+state;
                MainWindow window=null;
                try{
                    window=VisualShotNewWindow(false,VisualShotWidth(),VisualShotHeight());
                    window.LoadFileCore(fixture);VisualShotSettle(window);
                    window.SetMode(true);VisualShotSettle(window);
                    window.SetLiveMode(live);VisualShotSettle(window);
                    int at=window.text.IndexOf("짧음",StringComparison.Ordinal);
                    if(inside){
                        if(at<0)failures.Add(baseName+": fixture cell text was not found");
                        else window.source.CaretIndex=at+1;
                        window.source.ScrollToLine(3);
                    }else{
                        window.source.CaretIndex=0;
                        window.source.ScrollToVerticalOffset(0);
                    }
                    if(window.source.TextArea!=null&&window.source.TextArea.Caret!=null)window.source.TextArea.Caret.BringCaretToView();
                    window.UpdateTableTools();
                    VisualShotPump(300);VisualShotSettle(window);
                    bool tools=window.tableToolsHost.Visibility==Visibility.Visible;
                    if(inside&&!tools)failures.Add(baseName+": table tools row did not appear with the caret inside a cell");
                    if(!inside&&tools)failures.Add(baseName+": table tools row stayed visible with the caret outside the table");
                    checks.AppendLine(baseName+": mode="+(live?"live (caret line drawn as source)":"basic (every line formatted)")+" caret="+window.source.CaretIndex+" tableTools="+(tools?"visible":"collapsed")+" lines="+window.source.Document.LineCount);
                    foreach(double scale in VisualShotScales){
                        string name=baseName+(scale==1.0?"":"-150");
                        string description="Table fixture, "+mode+" mode, caret "+(inside?"inside a cell (table tools visible)":"outside the table")+", light theme";
                        VisualShotShoot(window,dir,name,scale,null,description,"group3 table basic/live",false,checks,failures,entries);
                    }
                }catch(Exception error){
                    failures.Add(baseName+": "+VisualShotError(error));
                }finally{
                    if(window!=null)window.Close();
                }
            }
        }
    }

    static bool VisualShotOpenMenuInternal(MenuItem item){
        try{
            var method=typeof(MenuItem).GetMethod("OpenMenu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            if(method==null)return false;
            object result=method.Invoke(item,null);
            return result is bool?(bool)result:true;
        }catch(Exception){return false;}
    }

    static bool VisualShotInvokeMenuClick(MenuItem item){
        try{
            var method=typeof(MenuItem).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            if(method==null)return false;
            method.Invoke(item,null);
            return true;
        }catch(Exception){return false;}
    }

    static string VisualShotMenuState(MainWindow window,MenuItem item,Popup popup){
        string state="IsLoaded="+item.IsLoaded+" submenuOpen="+item.IsSubmenuOpen+" role="+item.Role+" items="+item.Items.Count+" template="+(item.Template==null?"null":"set")+" menuFocus="+(window.topMenu==null?"?":window.topMenu.IsKeyboardFocusWithin.ToString())+" active="+window.IsActive;
        if(popup==null)return state+" popup=null";
        var child=popup.Child as FrameworkElement;
        state+=" popupOpen="+popup.IsOpen+" popupLoaded="+popup.IsLoaded+" staysOpen="+popup.StaysOpen+" transparent="+popup.AllowsTransparency+" animation="+popup.PopupAnimation+" placement="+popup.Placement+" child="+(popup.Child==null?"null":(popup.Child.GetType().Name+(child==null?"":" "+VisualShotNum(child.ActualWidth)+"x"+VisualShotNum(child.ActualHeight)+" visible="+child.IsVisible)));
        return state;
    }

    static double VisualShotWidth(){return 1080;}
    static double VisualShotHeight(){return 800;}

    static MainWindow VisualShotNewWindow(bool onScreen,double width,double height){
        var window=new MainWindow();
        window.WindowStartupLocation=WindowStartupLocation.Manual;
        window.ShowInTaskbar=false;
        window.ShowActivated=false;
        if(onScreen){window.Left=60;window.Top=60;}
        else{window.Left=-20000;window.Top=-20000;}
        window.Width=width;window.Height=height;
        window.Show();
        VisualShotSettle(window);
        return window;
    }

    static void VisualShotSettle(Window window){
        if(window==null)return;
        for(int i=0;i<3;i++){
            try{window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(delegate{}));}catch(Exception){}
            try{window.UpdateLayout();}catch(Exception){}
            LongLineBench.Idle();
        }
    }

    static void VisualShotPump(int milliseconds){
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        timer.Interval=TimeSpan.FromMilliseconds(milliseconds);
        timer.Tick+=delegate{timer.Stop();frame.Continue=false;};
        timer.Start();
        Dispatcher.PushFrame(frame);
        var idle=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{idle.Continue=false;}));
        Dispatcher.PushFrame(idle);
    }

    static Window VisualShotOwnedDialog(MainWindow window){
        var app=Application.Current;
        if(app==null)return null;
        foreach(Window candidate in app.Windows){
            if(Object.ReferenceEquals(candidate,window))continue;
            if(Object.ReferenceEquals(candidate.Owner,window)&&candidate.IsVisible)return candidate;
        }
        return null;
    }

    static void VisualShotEnsureLayout(FrameworkElement element){
        if(element==null)return;
        try{
            element.UpdateLayout();
            if(element.ActualWidth<=0||element.ActualHeight<=0){
                element.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
                element.Arrange(new Rect(0,0,element.DesiredSize.Width,element.DesiredSize.Height));
                element.UpdateLayout();
            }
        }catch(Exception){}
    }

    static Rect VisualShotRectInContent(FrameworkElement element,FrameworkElement content){
        if(element==null||content==null)return Rect.Empty;
        try{
            Point point=element.TransformToAncestor(content).Transform(new Point(0,0));
            return new Rect(point.X,point.Y,Math.Max(0,element.ActualWidth),Math.Max(0,element.ActualHeight));
        }catch(Exception){return Rect.Empty;}
    }

    static Rect VisualShotBoundsInContent(FrameworkElement element,FrameworkElement content){
        if(element==null||content==null)return Rect.Empty;
        try{
            Point point=content.PointFromScreen(element.PointToScreen(new Point(0,0)));
            return new Rect(point.X,point.Y,Math.Max(0,element.ActualWidth),Math.Max(0,element.ActualHeight));
        }catch(Exception){return Rect.Empty;}
    }

    static void VisualShotShoot(Window window,string dir,string name,double scale,List<VisualShotPopup> popups,string description,string group,bool darkExpected,StringBuilder checks,List<string> failures,List<VisualShotEntry> entries){
        try{
            VisualShotSettle(window);
            var content=window.Content as FrameworkElement;
            if(content==null){failures.Add(name+": window has no content");return;}
            double contentWidth=content.ActualWidth+content.Margin.Left+content.Margin.Right;
            double contentHeight=content.ActualHeight+content.Margin.Top+content.Margin.Bottom;
            if(contentWidth<10||contentHeight<10){failures.Add(name+": window content is not laid out ("+VisualShotNum(contentWidth)+"x"+VisualShotNum(contentHeight)+")");return;}
            int expectedWidth=(int)Math.Ceiling(contentWidth*scale);
            int expectedHeight=(int)Math.Ceiling(contentHeight*scale);
            var bitmap=VisualShotCompose(window,content,contentWidth,contentHeight,scale,popups);
            string path=Path.Combine(dir,name+".png");
            VisualShotSave(bitmap,path);
            int width,height;double ink;
            string scan=VisualShotScan(path,out width,out height,out ink);
            if(width!=expectedWidth||height!=expectedHeight)failures.Add(name+": PNG is "+width+"x"+height+", expected "+expectedWidth+"x"+expectedHeight+" at scale "+VisualShotNum(scale));
            if(ink<0.004)failures.Add(name+": image looks blank (ink fraction "+ink.ToString("0.0000",CultureInfo.InvariantCulture)+")");
            entries.Add(new VisualShotEntry{Group=group,File=name+".png",Description=description+"; "+(scale==1.0?"100%":"150%")+"; "+width+"x"+height+" px; ink="+ink.ToString("0.000",CultureInfo.InvariantCulture)});
            checks.AppendLine(name+".png: size="+width+"x"+height+" scale="+VisualShotNum(scale)+" ink="+ink.ToString("0.000",CultureInfo.InvariantCulture)+" "+scan);
            if(darkExpected&&scale==1.0&&popups!=null){
                foreach(var spec in popups){
                    if(!spec.ExpectedDark)continue;
                    string sample;
                    bool dark=VisualShotDarkAt(path,spec.Bounds,scale,out sample);
                    checks.AppendLine(spec.Label+": dark popup background sample "+sample+" "+(dark?"PASS":"FAIL"));
                    if(!dark)failures.Add(spec.Label+": menu popup background is not dark ("+sample+")");
                }
            }
        }catch(Exception error){
            failures.Add(name+": "+VisualShotError(error));
        }
    }

    static RenderTargetBitmap VisualShotCompose(Window window,FrameworkElement content,double contentWidth,double contentHeight,double scale,List<VisualShotPopup> popups){
        int pixelWidth=Math.Max(1,(int)Math.Ceiling(contentWidth*scale));
        int pixelHeight=Math.Max(1,(int)Math.Ceiling(contentHeight*scale));
        var bitmap=new RenderTargetBitmap(pixelWidth,pixelHeight,96*scale,96*scale,PixelFormats.Pbgra32);
        var background=new DrawingVisual();
        using(var context=background.RenderOpen())context.DrawRectangle(window.Background??Brushes.White,null,new Rect(0,0,contentWidth,contentHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        if(popups!=null){
            foreach(var spec in popups){
                if(spec==null||spec.Visual==null)continue;
                var image=VisualShotRenderVisual(spec.Visual,scale);
                if(image==null)continue;
                var layer=new DrawingVisual();
                using(var context=layer.RenderOpen())context.DrawImage(image,new Rect(spec.Bounds.Left,spec.Bounds.Top,Math.Max(1,spec.Bounds.Width),Math.Max(1,spec.Bounds.Height)));
                bitmap.Render(layer);
            }
        }
        return bitmap;
    }

    static BitmapSource VisualShotRenderVisual(FrameworkElement visual,double scale){
        if(visual==null||visual.ActualWidth<=0||visual.ActualHeight<=0)return null;
        int width=Math.Max(1,(int)Math.Ceiling(visual.ActualWidth*scale));
        int height=Math.Max(1,(int)Math.Ceiling(visual.ActualHeight*scale));
        var bitmap=new RenderTargetBitmap(width,height,96*scale,96*scale,PixelFormats.Pbgra32);
        try{bitmap.Render(visual);}catch(Exception){return null;}
        return bitmap;
    }

    static void VisualShotSave(BitmapSource bitmap,string path){
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path))encoder.Save(stream);
    }

    static string VisualShotScan(string path,out int width,out int height,out double ink){
        width=0;height=0;ink=0;
        try{
            using(var stream=File.OpenRead(path)){
                var decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var frame=decoder.Frames[0];
                width=frame.PixelWidth;height=frame.PixelHeight;
                int stride=width*4;
                var pixels=new byte[stride*height];
                frame.CopyPixels(pixels,stride,0);
                int step=Math.Max(1,Math.Min(width,height)/80);
                int total=0,changed=0;
                int baseB=pixels[0],baseG=pixels[1],baseR=pixels[2];
                for(int y=0;y<height;y+=step){
                    for(int x=0;x<width;x+=step){
                        int at=y*stride+x*4;total++;
                        int delta=Math.Abs(pixels[at]-baseB)+Math.Abs(pixels[at+1]-baseG)+Math.Abs(pixels[at+2]-baseR);
                        if(delta>24)changed++;
                    }
                }
                if(total>0)ink=changed/(double)total;
            }
            return "nonblank="+(ink>=0.004?"PASS":"FAIL");
        }catch(Exception error){
            return "decode failed: "+error.Message;
        }
    }

    static bool VisualShotDarkAt(string path,Rect bounds,double scale,out string detail){
        detail="";
        double midX=(bounds.Left+bounds.Right)/2;
        double midY=(bounds.Top+bounds.Bottom)/2;
        double[] xs={midX,midX,bounds.Left+3,bounds.Right-3};
        double[] ys={bounds.Top+3,bounds.Bottom-3,bounds.Top+10,bounds.Top+10};
        try{
            using(var stream=File.OpenRead(path)){
                var decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var frame=decoder.Frames[0];
                var parts=new List<string>();
                bool all=true;
                for(int i=0;i<xs.Length;i++){
                    int x=(int)Math.Round(xs[i]*scale),y=(int)Math.Round(ys[i]*scale);
                    if(x<0||y<0||x>=frame.PixelWidth||y>=frame.PixelHeight){all=false;parts.Add("outside-image");continue;}
                    var pixel=new byte[4];
                    frame.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
                    int average=(pixel[0]+pixel[1]+pixel[2])/3;
                    bool dark=pixel[3]>=200&&average<=110;
                    if(!dark)all=false;
                    parts.Add("#"+x+","+y+" avg="+average+(pixel[3]>=200?"":" alpha="+pixel[3]));
                }
                detail=String.Join(" ",parts.ToArray());
                return all;
            }
        }catch(Exception error){
            detail="decode failed: "+error.Message;
            return false;
        }
    }

    static void VisualShotCheckPopup(Window window,VisualShotPopup spec,FrameworkElement content,StringBuilder checks,List<string> failures){
        Rect box=spec.Bounds;
        bool insideWindow=box.Left>=-spec.Tolerance&&box.Top>=-spec.Tolerance&&box.Right<=content.ActualWidth+spec.Tolerance&&box.Bottom<=content.ActualHeight+spec.Tolerance;
        string placed="boundsInWindow="+VisualShotNum(box.Left)+","+VisualShotNum(box.Top)+".."+VisualShotNum(box.Right)+","+VisualShotNum(box.Bottom)+" content="+VisualShotNum(content.ActualWidth)+"x"+VisualShotNum(content.ActualHeight)+" insideWindow="+insideWindow+" tolerance="+VisualShotNum(spec.Tolerance);
        if(spec.Manual){
            if(!insideWindow)failures.Add(spec.Label+": manually laid out menu leaves the window ("+placed+")");
            checks.AppendLine(spec.Label+": manual probe layout (the system popup cannot stay open on the hidden desktop) "+placed);
            return;
        }
        string detail="";
        bool screenOk=false;
        try{
            Point p0=spec.Visual.PointToScreen(new Point(0,0));
            Point p1=spec.Visual.PointToScreen(new Point(Math.Max(1,spec.Visual.ActualWidth),Math.Max(1,spec.Visual.ActualHeight)));
            var screen=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)((p0.X+p1.X)/2),(int)((p0.Y+p1.Y)/2)));
            var work=screen.WorkingArea;
            screenOk=p0.X>=work.Left-1&&p0.Y>=work.Top-1&&p1.X<=work.Right+1&&p1.Y<=work.Bottom+1;
            detail="screenRect="+VisualShotNum(p0.X)+","+VisualShotNum(p0.Y)+".."+VisualShotNum(p1.X)+","+VisualShotNum(p1.Y)+" workArea="+work.Left+","+work.Top+".."+work.Right+","+work.Bottom+" insideScreen="+screenOk;
            if(!screenOk)failures.Add(spec.Label+": popup leaves the screen ("+detail+")");
        }catch(Exception error){
            detail="screen measure failed: "+error.Message;
            failures.Add(spec.Label+": popup screen position could not be measured ("+detail+")");
        }
        detail+=" "+placed;
        if(!insideWindow)failures.Add(spec.Label+": popup leaves the window ("+detail+")");
        if(spec.BelowAnchor){
            bool below=false,measured=false;string screenSpace="";
            try{
                if(spec.AnchorVisual!=null&&spec.Visual!=null){
                    double anchorHeight=Math.Max(1,spec.AnchorVisual.ActualHeight);
                    Point anchorTopLeft=spec.AnchorVisual.PointToScreen(new Point(0,0));
                    Point anchorBottom=spec.AnchorVisual.PointToScreen(new Point(0,anchorHeight));
                    Point popupTopLeft=spec.Visual.PointToScreen(new Point(0,0));
                    below=popupTopLeft.Y>=anchorBottom.Y-6;
                    measured=true;
                    screenSpace=" screenAnchor=("+VisualShotNum(anchorTopLeft.X)+","+VisualShotNum(anchorTopLeft.Y)+") itemBottomScreen="+VisualShotNum(anchorBottom.Y)+" popupTopScreen="+VisualShotNum(popupTopLeft.Y)+" popupBottomScreen="+VisualShotNum(popupTopLeft.Y+spec.Visual.ActualHeight);
                }
            }catch(Exception error){screenSpace=" screen-space measure failed: "+error.Message;}
            detail+=" belowMenuBar="+below+" anchorBottom="+VisualShotNum(spec.Anchor.Bottom)+screenSpace;
            if(measured&&!below){
                string finding=spec.Label+": the top-menu dropdown opens at the menu-bar row instead of below its item"+screenSpace;
                if(VisualShotFindings!=null&&!VisualShotFindings.Contains(finding))VisualShotFindings.Add(finding);
                failures.Add(spec.Label+": top-menu popup is not below its menu item (popupTopInWindow="+VisualShotNum(box.Top)+" itemBottomInWindow="+VisualShotNum(spec.Anchor.Bottom)+")"+screenSpace);
                checks.AppendLine(spec.Label+": FAIL menu popup is not below its menu item (popupTopInWindow="+VisualShotNum(box.Top)+" itemBottomInWindow="+VisualShotNum(spec.Anchor.Bottom)+")"+screenSpace);
            }else if(!measured){
                failures.Add(spec.Label+": popup position in screen space could not be measured ("+screenSpace+")");
            }
        }
        checks.AppendLine(spec.Label+": "+detail);
    }

    static int VisualShotFindAnchorLine(ICSharpCode.AvalonEdit.Document.TextDocument document){
        if(document==null)return 0;
        var table=new List<int>();var code=new List<int>();var list=new List<int>();
        for(int number=1;number<=document.LineCount;number++){
            var line=document.GetLineByNumber(number);
            string source=document.GetText(line.Offset,line.Length);
            string text=source.Trim();
            if(text.Length>=3&&text.IndexOf('|')>=0&&text.Replace("|","").Replace(":","").Replace("-","").Replace(" ","").Length==0)table.Add(number);
            else if(text.StartsWith("```",StringComparison.Ordinal))code.Add(number);
            else if(text.StartsWith("- ",StringComparison.Ordinal)||text.StartsWith("* ",StringComparison.Ordinal)||text.StartsWith("> ",StringComparison.Ordinal))list.Add(number);
        }
        int target=Math.Max(1,(int)Math.Round(document.LineCount*0.55));
        int best=VisualShotNearest(table,target);
        if(best==0)best=VisualShotNearest(code,target);
        if(best==0)best=VisualShotNearest(list,target);
        if(best==0)best=Math.Max(1,Math.Min(document.LineCount,target));
        return best;
    }

    static int VisualShotNearest(List<int> lines,int target){
        int best=0;
        foreach(int line in lines)if(best==0||Math.Abs(line-target)<Math.Abs(best-target))best=line;
        return best;
    }

    static string VisualShotNum(double value){return value.ToString("0.##",CultureInfo.InvariantCulture);}

    static string VisualShotError(Exception error){return error.GetType().Name+": "+error.Message+" @ "+VisualShotOneLine(error.StackTrace);}

    static string VisualShotOneLine(string value){
        if(value==null)return "";
        int at=value.IndexOfAny(new char[]{'\r','\n'});
        return at<0?value:value.Substring(0,at);
    }
}
}
