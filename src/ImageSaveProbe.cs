using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MDNote {
// --image-save-probe <dir>: the two save rules of the staged pictures (user, 2026-11-04). (1) A link
// inside a fenced code block or an inline code span is literal text: its picture is not moved or
// copied and the link stays as written. (2) A staged picture that another open document (another
// tab or window) also links is COPIED into the saved document's folder and stays in the temp
// folder; the document that saves last, with nobody else linking it, moves it. No clipboard, no
// real input: the product's own Save runs through the SaveTargetPicker seam, like DropProbe.
// Each case has its own temp folder (MainWindow.ImageTempFolderOverride) and its own target
// folder under <dir>.
internal static class ImageSaveProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly StringBuilder Report=new StringBuilder();
    static readonly StringBuilder Failures=new StringBuilder();
    static bool All=true;static int Passes,Fails;static Exception DispatchError;static Stopwatch Clock;static string OutDir;

    static void Check(string name,bool ok,string expected,string actual){
        if(ok)Passes++;else Fails++;
        All&=ok;
        Report.Append(ok?"PASS ":"FAIL ").Append(name);
        if(!ok)Report.Append(" | expected: ").Append(OneLine(expected)).Append(" | actual: ").Append(OneLine(actual));
        Report.Append('\n');
        if(!ok)Failures.Append(name).Append("\n  expected: ").Append(OneLine(expected)).Append("\n  actual:   ").Append(OneLine(actual)).Append('\n');
    }
    static string OneLine(string value){
        if(value==null)return "-";
        string one=value.Replace("\r","\\r").Replace("\n","\\n");
        return one.Length<=240?one:one.Substring(0,237)+"...";
    }
    static string Nl(string value){return value==null?null:value.Replace("\r\n","\n");}
    static void Pump(){
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));
    }
    static FieldInfo NeedField(object owner,string name){
        FieldInfo field=owner.GetType().GetField(name,Members);
        if(field==null)throw new MissingMemberException(owner.GetType().Name+" field "+name);
        return field;
    }
    static T Field<T>(object owner,string name){return (T)NeedField(owner,name).GetValue(owner);}
    static void SetField(object owner,string name,object value){NeedField(owner,name).SetValue(owner,value);}
    static object Call(MainWindow window,string name,params object[] args){
        MethodInfo method=typeof(MainWindow).GetMethod(name,Members);
        if(method==null)throw new MissingMemberException("MainWindow method "+name);
        return method.Invoke(window,args);
    }
    static SourceEditor Source(MainWindow window){return Field<SourceEditor>(window,"source");}
    static string Text(MainWindow window){return Source(window).Document.Text;}
    static string ReadDisk(string file){return File.ReadAllText(file,new UTF8Encoding(false));}
    static int FileCount(string folder){return Directory.Exists(folder)?Directory.GetFiles(folder).Length:0;}
    static void ResetFolder(string folder){
        if(Directory.Exists(folder))Directory.Delete(folder,true);
        Directory.CreateDirectory(folder);
    }
    static void WriteImage(string file){
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(10,10,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var visual=new System.Windows.Media.DrawingVisual();
        using(var context=visual.RenderOpen())context.DrawRectangle(System.Windows.Media.Brushes.SteelBlue,null,new Rect(0,0,10,10));
        bitmap.Render(visual);
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using(var stream=File.Create(file))encoder.Save(stream);
    }
    static bool SameBytes(string left,string right){
        try{
            byte[] one=File.ReadAllBytes(left),two=File.ReadAllBytes(right);
            if(one.Length!=two.Length)return false;
            for(int i=0;i<one.Length;i++)if(one[i]!=two[i])return false;
            return true;
        }catch(Exception){return false;}
    }
    // The link text a paste writes for a picture in the shared temp folder (absolute path), from the
    // product's own helper.
    static string RefOf(string picture){return MarkdownCommands.ImageReference("",picture);}
    static MainWindow NewWindow(){
        MainWindow window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        window.Show();
        Pump();
        window.SetMode(true);
        Pump();
        return window;
    }
    static void ResetEditor(MainWindow window,string text){
        window.SetText(text);
        Pump();
        if(!Field<bool>(window,"editing")){window.SetMode(true);Pump();}
    }
    static bool SaveWith(MainWindow window,bool saveAs,string pickTarget,out string error){
        error=null;
        Func<string,string> oldPicker=MainWindow.SaveTargetPicker;
        if(pickTarget!=null)MainWindow.SaveTargetPicker=delegate(string name){return pickTarget;};
        try{return (bool)Call(window,"Save",saveAs);}
        catch(TargetInvocationException ex){error=ex.InnerException!=null?ex.InnerException.GetType().Name+": "+ex.InnerException.Message:ex.Message;return false;}
        finally{MainWindow.SaveTargetPicker=oldPicker;Pump();}
    }
    // Every tab is marked clean first, so closing the window never asks about a document.
    static void SafeClose(MainWindow window){
        if(window==null)return;
        try{
            Call(window,"FlushDocumentChanges");
            Call(window,"CaptureTab");
            IList tabs=Field<object>(window,"documents") as IList;
            if(tabs!=null){
                foreach(object tab in tabs){
                    SetField(tab,"Saved",Field<string>(tab,"Text"));
                    SetField(tab,"Dirty",false);
                }
            }
            // "text" is a property (a lazy snapshot of the editor), not a field.
            SetField(window,"saved",(string)typeof(MainWindow).GetProperty("text",Members).GetValue(window,null));
            SetField(window,"recoveredDocument",false);
            window.Close();
            Pump();
        }catch(Exception ex){Check("close window",false,"closed",ex.GetType().Name+": "+ex.Message);}
    }

    /* One window, one document: the text is template with {1},{2},... replaced by the absolute link
       of the staged picture number n; moved[n-1] says whether the save must move that picture into
       doc\ (relative link) or leave it in the temp folder (link unchanged). */
    static void RunCase(string name,string template,bool[] moved){
        MainWindow window=null;
        try{
            string caseDir=Path.Combine(OutDir,name);
            string tempDir=Path.Combine(Path.Combine(OutDir,"temp-images"),name);
            ResetFolder(caseDir);ResetFolder(tempDir);
            MainWindow.ImageTempFolderOverride=tempDir;
            int count=moved.Length;
            string[] pictures=new string[count],refs=new string[count],names=new string[count];
            string text=template;
            for(int i=0;i<count;i++){
                names[i]="pic-"+name+"-"+(i+1)+".png";
                pictures[i]=Path.Combine(tempDir,names[i]);
                WriteImage(pictures[i]);
                refs[i]=RefOf(pictures[i]);
                text=text.Replace("{"+(i+1)+"}",refs[i]);
            }
            string target=Path.Combine(caseDir,"doc.md"),docFolder=Path.Combine(caseDir,"doc");
            string expectText=template;
            int movedCount=0;
            for(int i=0;i<count;i++){
                if(moved[i]){expectText=expectText.Replace("{"+(i+1)+"}","doc/"+names[i]);movedCount++;}
                else expectText=expectText.Replace("{"+(i+1)+"}",refs[i]);
            }
            window=NewWindow();
            ResetEditor(window,text);
            string error;
            bool saved=SaveWith(window,false,target,out error);
            string after=Text(window);
            Check(name+" save succeeded",saved,"saved=True","saved="+saved+" "+error);
            Check(name+" saved text",Nl(after)==Nl(expectText),expectText,after);
            Check(name+" file on disk holds the same text",File.Exists(target)&&Nl(ReadDisk(target))==Nl(expectText),expectText,File.Exists(target)?ReadDisk(target):"(no file)");
            for(int i=0;i<count;i++){
                bool inTemp=File.Exists(pictures[i]),inDoc=File.Exists(Path.Combine(docFolder,names[i]));
                if(moved[i])Check(name+" picture "+(i+1)+" moved into doc\\",!inTemp&&inDoc,"temp=False doc=True","temp="+inTemp+" doc="+inDoc);
                else Check(name+" picture "+(i+1)+" stays in temp, not in doc\\",inTemp&&!inDoc,"temp=True doc=False","temp="+inTemp+" doc="+inDoc);
            }
            Check(name+" doc\\ holds only the moved pictures",FileCount(docFolder)==movedCount,"files="+movedCount,"files="+FileCount(docFolder));
        }catch(Exception ex){Check(name+" run",false,"no exception",ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(window);}
    }

    /* Case 6: two documents (two tabs of one window, or two windows) link the same staged picture.
       Saving the first COPIES it (the other document still needs the temp file); saving the second,
       when nobody else links it any more, MOVES it. */
    static void RunShared(string name,bool twoWindows){
        MainWindow windowA=null,windowB=null;
        try{
            string dirA=Path.Combine(OutDir,name+"-a"),dirB=Path.Combine(OutDir,name+"-b");
            string tempDir=Path.Combine(Path.Combine(OutDir,"temp-images"),name);
            ResetFolder(dirA);ResetFolder(dirB);ResetFolder(tempDir);
            MainWindow.ImageTempFolderOverride=tempDir;
            string fileName="pic-"+name+".png";
            string picture=Path.Combine(tempDir,fileName);
            WriteImage(picture);
            string reference=RefOf(picture);
            string textA="![a]("+reference+")\n",textB="![b]("+reference+")\n";
            string targetA=Path.Combine(dirA,"docA.md"),targetB=Path.Combine(dirB,"docB.md");
            string folderA=Path.Combine(dirA,"docA"),folderB=Path.Combine(dirB,"docB");
            string relA="![a](docA/"+fileName+")\n",relB="![b](docB/"+fileName+")\n";
            windowA=NewWindow();
            ResetEditor(windowA,textA);
            object tabA=null,tabB=null;
            MainWindow saverB;
            if(twoWindows){
                windowB=NewWindow();
                ResetEditor(windowB,textB);
                saverB=windowB;
            }else{
                IList tabs=Field<object>(windowA,"documents") as IList;
                tabA=tabs[0];
                Call(windowA,"CreateTab");Pump();
                tabB=tabs[tabs.Count-1];
                ResetEditor(windowA,textB);
                Call(windowA,"SwitchTab",new object[]{tabA});Pump();
                saverB=windowA;
            }
            Check(name+" setup: first document shows its text",Nl(Text(windowA))==Nl(textA),textA,Text(windowA));
            string error;
            bool savedA=SaveWith(windowA,false,targetA,out error);
            Check(name+" first save succeeded",savedA,"saved=True","saved="+savedA+" "+error);
            string copyA=Path.Combine(folderA,fileName);
            Check(name+" first save copied the picture (docA\\ has it, temp still has it)",File.Exists(copyA)&&SameBytes(picture,copyA)&&File.Exists(picture),"docA=True temp=True","docA="+File.Exists(copyA)+" temp="+File.Exists(picture));
            Check(name+" first document link is relative",Nl(Text(windowA))==relA,relA,Text(windowA));
            Check(name+" first file on disk holds the relative link",File.Exists(targetA)&&Nl(ReadDisk(targetA))==relA,relA,File.Exists(targetA)?ReadDisk(targetA):"(no file)");
            if(twoWindows){
                Check(name+" second document text unchanged",Nl(Text(windowB))==textB,textB,Text(windowB));
            }else{
                Call(windowA,"SwitchTab",new object[]{tabB});Pump();
                Check(name+" second tab text unchanged",Nl(Text(windowA))==textB,textB,Text(windowA));
            }
            Check(name+" temp picture still there before the second save",File.Exists(picture),"temp=True","temp="+File.Exists(picture));
            bool savedB=SaveWith(saverB,false,targetB,out error);
            Check(name+" second save succeeded",savedB,"saved=True","saved="+savedB+" "+error);
            string movedB=Path.Combine(folderB,fileName);
            Check(name+" second save moved the picture (docB\\ has it, temp is empty of it)",File.Exists(movedB)&&!File.Exists(picture),"docB=True temp=False","docB="+File.Exists(movedB)+" temp="+File.Exists(picture));
            Check(name+" second document link is relative",Nl(Text(saverB))==relB,relB,Text(saverB));
            Check(name+" second file on disk holds the relative link",File.Exists(targetB)&&Nl(ReadDisk(targetB))==relB,relB,File.Exists(targetB)?ReadDisk(targetB):"(no file)");
            Check(name+" first copy in docA\\ is untouched",File.Exists(copyA)&&FileCount(folderA)==1,"docA files=1","docA files="+FileCount(folderA));
        }catch(Exception ex){Check(name+" run",false,"no exception",ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(windowB);SafeClose(windowA);}
    }

    /* Case 7: the shared picture is copied by a Save As that is then refused (the target is open in
       another window, which also links the picture). The refused save must leave no copy behind, the
       temp picture and every link as they were. */
    static void RunFailedShared(){
        string name="case7";
        MainWindow holder=null,saver=null;
        try{
            string dir=Path.Combine(OutDir,name);
            string tempDir=Path.Combine(Path.Combine(OutDir,"temp-images"),name);
            ResetFolder(dir);ResetFolder(tempDir);
            MainWindow.ImageTempFolderOverride=tempDir;
            string fileName="pic-"+name+".png";
            string picture=Path.Combine(tempDir,fileName);
            WriteImage(picture);
            string reference=RefOf(picture);
            string heldDoc=Path.Combine(dir,"held.md");
            File.WriteAllText(heldDoc,"other\n",new UTF8Encoding(false));
            holder=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            holder.Show();
            Call(holder,"LoadFileCore",heldDoc);
            Pump();
            holder.SetMode(true);Pump();
            string holderText="![h]("+reference+")\n";
            ResetEditor(holder,holderText);
            saver=NewWindow();
            string saverText="![s]("+reference+")\n";
            ResetEditor(saver,saverText);
            string notice=null;
            Action<string> oldNotice=MainWindow.SaveTargetInUseNotice;
            MainWindow.SaveTargetInUseNotice=delegate(string message){notice=message;};
            bool saved;string error;
            try{saved=SaveWith(saver,true,heldDoc,out error);}finally{MainWindow.SaveTargetInUseNotice=oldNotice;}
            string heldFolder=Path.Combine(dir,"held");
            Check(name+" save refused (the target is open elsewhere)",!saved&&notice!=null,"saved=False, notice set","saved="+saved+", notice="+(notice==null?"(none)":"set")+" "+error);
            Check(name+" no copy left in held\\",FileCount(heldFolder)==0,"files=0","files="+FileCount(heldFolder));
            Check(name+" temp picture still there",File.Exists(picture),"temp=True","temp="+File.Exists(picture));
            Check(name+" saving document's link restored",Nl(Text(saver))==saverText,saverText,Text(saver));
            Check(name+" other document text unchanged",Nl(Text(holder))==holderText,holderText,Text(holder));
            Check(name+" the open file on disk is untouched",Nl(ReadDisk(heldDoc))=="other\n","other\\n",ReadDisk(heldDoc));
        }catch(Exception ex){Check(name+" run",false,"no exception",ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(saver);SafeClose(holder);}
    }

    static void WriteReport(){
        string marker=Path.Combine(OutDir,"image-save-"+(All?"PASS":"FAIL")+".txt");
        string opposite=Path.Combine(OutDir,"image-save-"+(All?"FAIL":"PASS")+".txt");
        try{if(File.Exists(opposite))File.Delete(opposite);}catch(Exception){}
        Report.Append('\n').Append("NOTES\n");
        Report.Append("- Each case points MainWindow.ImageTempFolderOverride at <dir>\\temp-images\\<case>, writes its own tiny PNGs there and saves an untitled document through the window's real Save (SaveTargetPicker seam) into <dir>\\<case>\\doc.md.\n");
        Report.Append("- case6 uses two tabs of one window (CreateTab/SwitchTab); case6w uses two MainWindow instances (TempImagesLinkedElsewhere reads Application.Current.Windows); case7 is a refused Save As (SaveTargetInUseNotice) whose picture is also linked by the other open window.\n");
        Report.Append("- Elapsed ").Append(Clock.ElapsedMilliseconds).Append(" ms.\n");
        File.WriteAllText(Path.Combine(OutDir,"image-save-probe.txt"),Report.ToString(),new UTF8Encoding(false));
        string head="ImageSaveProbe "+(All?"PASS":"FAIL")+"  "+Passes+" PASS, "+Fails+" FAIL, "+Clock.ElapsedMilliseconds+" ms\n";
        File.WriteAllText(marker,All?head:head+Failures.ToString(),new UTF8Encoding(false));
    }
    internal static int Run(string dir){
        OutDir=Path.GetFullPath(dir);
        Directory.CreateDirectory(OutDir);
        Clock=Stopwatch.StartNew();
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Dispatcher.CurrentDispatcher.UnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs args){DispatchError=args.Exception;args.Handled=true;};
        Report.Append("ImageSaveProbe  ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
        try{
            Directory.CreateDirectory(Path.Combine(OutDir,"temp-images"));
            RunCase("case1","```\n![a]({1})\n```\n",new bool[]{false});
            RunCase("case2","text `![a]({1})` more",new bool[]{false});
            RunCase("case3","![a]({1})\n\n```\n![b]({2})\n```\n![c]({3})",new bool[]{true,false,true});
            RunCase("case4","~~~md\n![a]({1})\n```\n![b]({2})\n~~~\n![c]({3})",new bool[]{false,false,true});
            RunCase("case5","![a]({1})",new bool[]{true});
            // Review cases (2026-11-04 web Argon): escaped backticks, quote blank lines, indented fences, CRLF.
            RunCase("caseA1","a \\` ![x]({1}) `b` end",new bool[]{true});
            // "\``" leaves a 1-backtick run that a 2-backtick closer cannot close (CommonMark): not code.
            RunCase("caseA2","\\``code ![x]({1}) `` tail",new bool[]{true});
            // "\```" leaves a 2-backtick run that the 2-backtick closer closes: code.
            RunCase("caseA3","\\```code ![x]({1}) `` tail",new bool[]{false});
            RunCase("caseB","`open ![x]({1})\n>\n![x]({2}) `close",new bool[]{true,true});
            RunCase("caseC","- item\n\n    ```\n    ![x]({1})\n    ```\n\n![x]({2})",new bool[]{false,true});
            RunCase("caseD",">\t~~~~\n> ![x]({1})\n> ~~~\n> ~~~~~\n![x]({2})",new bool[]{false,true});
            RunCase("caseE","```\r\n![a]({1})\r\n```\r\n![b]({2})",new bool[]{false,true});
            RunShared("case6",false);
            RunShared("case6w",true);
            RunFailedShared();
            if(DispatchError!=null)Check("dispatcher exception",false,"none",DispatchError.GetType().Name+": "+DispatchError.Message);
        }catch(Exception ex){Check("probe run",false,"no exception",ex.GetType().Name+": "+ex.Message);}
        MainWindow.ImageTempFolderOverride=null;
        Clock.Stop();
        Report.Append('\n').Append("SUMMARY: ").Append(Passes).Append(" PASS, ").Append(Fails).Append(" FAIL, ").Append(Clock.ElapsedMilliseconds).Append(" ms\n");
        WriteReport();
        return All?0:1;
    }
}
}
