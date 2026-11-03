using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// --drop-probe <dir>: release test B9 (drag and drop). Constructs DataObjects with the real
// PreviewDrop/Drop routed events on the live source surface at points computed from the editor,
// then reads the document text, caret, history and the notice hook. Generated files stay under
// <dir>\drop-fixture.
internal static class DropProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly StringBuilder Report=new StringBuilder();
    static bool All=true;static int Passes,Fails;static Exception DispatchError;static Stopwatch Clock;static string OutDir;
    const string FixtureText="# 제목 줄\n\n첫 문단 줄입니다.\n둘째 문단 줄입니다.\n\n- 목록 항목 하나\n- 목록 항목 둘\n\n| A | B |\n|---|---|\n| a1 | b1 |\n| a2 | b2 |\n\n```text\ncode line one\ncode line two\n```\n\n마지막 문단.";
    const string TxtText="txt body\n",PickText="pick body\n",UnsavedText="새 문서 첫 줄입니다.\n둘째 줄입니다.";
    const string SvgText="<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"2\" height=\"2\"/></svg>\n";
    static string FixtureDir,DocPath,SamePath,SubPath,JpgPath,GifPath,SvgPath,PickPath,TxtPath;
    static string SameAlt,SameRef,SubAlt,SubRef,JpgAlt,JpgRef,GifAlt,GifRef,OtherPath,SameFull,UnsavedRef;
    static string OutsidePath,ClashPathA,ClashPathB,OutsideAlt,OutsideRef;
    static string OutsideDir,DocImageFolder,SameRefCopy,SubRefCopy,JpgRefCopy,GifRefCopy;

    sealed class DropOutcome {internal bool Handled;internal bool PreviewHandled;internal DragDropEffects Effects;internal Exception Error;}

    static void Check(string name,bool ok,string detail){
        if(ok)Passes++;else Fails++;
        All&=ok;
        Report.Append(ok?"PASS ":"FAIL ").Append(name);
        if(detail!=null&&detail.Length>0)Report.Append(": ").Append(OneLine(detail));
        Report.Append('\n');
    }
    static string OneLine(string value){return value==null?"":value.Replace("\r","\\r").Replace("\n","\\n");}
    static string Excerpt(string value){
        if(value==null)return "-";
        string one=OneLine(value);
        return one.Length<=180?one:one.Substring(0,177)+"...";
    }
    static void Pump(){
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));
    }
    static bool DrainDispatch(string where){
        if(DispatchError==null)return true;
        Exception error=DispatchError;DispatchError=null;
        Check(where+" dispatcher exception",false,error.GetType().Name+": "+error.Message);
        return false;
    }
    static FieldInfo NeedField(string name){
        FieldInfo field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("MainWindow field "+name);
        return field;
    }
    static T Field<T>(MainWindow window,string name){return (T)NeedField(name).GetValue(window);}
    static void SetField(MainWindow window,string name,object value){NeedField(name).SetValue(window,value);}
    static PropertyInfo NeedProperty(string name){
        PropertyInfo property=typeof(MainWindow).GetProperty(name,Members);
        if(property==null)throw new MissingMemberException("MainWindow property "+name);
        return property;
    }
    static T Prop<T>(MainWindow window,string name){return (T)NeedProperty(name).GetValue(window,null);}
    static object Call(MainWindow window,string name,params object[] args){
        MethodInfo method=typeof(MainWindow).GetMethod(name,Members);
        if(method==null)throw new MissingMemberException("MainWindow method "+name);
        return method.Invoke(window,args);
    }
    static SourceEditor Source(MainWindow window){return Field<SourceEditor>(window,"source");}
    static string Text(MainWindow window){return Source(window).Document.Text;}
    static string LineText(SourceEditor editor,int line){
        if(line<1||line>editor.Document.LineCount)return "";
        return editor.Document.GetText(editor.Document.GetLineByNumber(line));
    }
    static int LineOf(SourceEditor editor,int offset){
        if(offset<0||offset>editor.Document.TextLength)return -1;
        return editor.Document.GetLineByOffset(offset).LineNumber;
    }
    static int InsertStart(string before,string after){
        int at=0,limit=Math.Min(before.Length,after.Length);
        while(at<limit&&before[at]==after[at])at++;
        return at;
    }
    static string InsertedText(string before,string after){
        int at=InsertStart(before,after);
        int suffix=0;
        while(suffix<before.Length-at&&suffix<after.Length-at&&before[before.Length-1-suffix]==after[after.Length-1-suffix])suffix++;
        int removed=before.Length-at-suffix;
        if(removed!=0)return "<replaced "+removed+" chars>";
        return after.Substring(at,after.Length-at-suffix);
    }
    static int CountChar(string value,char target){int count=0;if(value!=null)foreach(char ch in value)if(ch==target)count++;return count;}
    static string EscapeRef(string value){return value.Replace("%","%25").Replace("(","%28").Replace(")","%29").Replace(" ","%20");}
    static string Markup(string alt,string reference){return "!["+alt+"]("+reference+")";}
    // The internal DragEventArgs constructor is not public; build one through reflection so the
    // real handlers see a real routed event with a controlled drop point. The 4-argument form on
    // older builds has no point parameter, so the private point field is set afterwards.
    static DragEventArgs NewDragArgs(IDataObject data,DragDropKeyStates keys,DragDropEffects allowed,DependencyObject target,Point point){
        ConstructorInfo chosen=null;
        foreach(ConstructorInfo candidate in typeof(DragEventArgs).GetConstructors(Members)){
            ParameterInfo[] parameters=candidate.GetParameters();
            bool dataParameter=false,targetParameter=false,pointParameter=false;
            foreach(ParameterInfo parameter in parameters){
                if(parameter.ParameterType==typeof(IDataObject))dataParameter=true;
                else if(parameter.ParameterType==typeof(DependencyObject))targetParameter=true;
                else if(parameter.ParameterType==typeof(Point))pointParameter=true;
            }
            if(dataParameter&&targetParameter&&pointParameter&&(chosen==null||parameters.Length>chosen.GetParameters().Length))chosen=candidate;
        }
        if(chosen==null){
            foreach(ConstructorInfo candidate in typeof(DragEventArgs).GetConstructors(Members)){
                ParameterInfo[] parameters=candidate.GetParameters();
                bool dataParameter=false,targetParameter=false;
                foreach(ParameterInfo parameter in parameters){
                    if(parameter.ParameterType==typeof(IDataObject))dataParameter=true;
                    else if(parameter.ParameterType==typeof(DependencyObject))targetParameter=true;
                }
                if(dataParameter&&targetParameter&&(chosen==null||parameters.Length>chosen.GetParameters().Length))chosen=candidate;
            }
        }
        if(chosen==null)throw new MissingMemberException("DragEventArgs usable constructor");
        ParameterInfo[] form=chosen.GetParameters();
        object[] values=new object[form.Length];
        bool hasPoint=false;
        for(int i=0;i<form.Length;i++){
            Type type=form[i].ParameterType;
            if(type==typeof(IDataObject))values[i]=data;
            else if(type==typeof(DragDropKeyStates))values[i]=keys;
            else if(type==typeof(DragDropEffects))values[i]=allowed;
            else if(type==typeof(DependencyObject))values[i]=target;
            else if(type==typeof(Point)){values[i]=point;hasPoint=true;}
            else if(type==typeof(bool))values[i]=false;
            else values[i]=null;
        }
        DragEventArgs args=(DragEventArgs)chosen.Invoke(values);
        if(!hasPoint){
            FieldInfo pointField=typeof(DragEventArgs).GetField("_dropPoint",Members);
            if(pointField==null)foreach(FieldInfo field in typeof(DragEventArgs).GetFields(Members))if(field.FieldType==typeof(Point)){pointField=field;break;}
            if(pointField==null)throw new MissingFieldException("DragEventArgs drop point field");
            pointField.SetValue(args,point);
        }
        return args;
    }
    // A real drop sends PreviewDrop down the tree and, when the source does not consume it, Drop
    // back up. Raising the two routed events on the TextArea in that order drives the same app and
    // vendor handlers (the window's Drop handler is on that bubble route).
    static DropOutcome RaiseDrop(MainWindow window,DataObject data,Point viewPoint,DragDropEffects allowed,DragDropKeyStates keys,bool bubble){
        DropOutcome outcome=new DropOutcome();
        try{
            SourceEditor editor=Source(window);
            var area=editor.TextArea;
            var view=area.TextView;
            DragEventArgs preview=NewDragArgs(data,keys,allowed,view,viewPoint);
            preview.RoutedEvent=DragDrop.PreviewDropEvent;preview.Source=area;
            area.RaiseEvent(preview);
            outcome.PreviewHandled=preview.Handled;outcome.Handled=preview.Handled;outcome.Effects=preview.Effects;
            if(bubble&&!preview.Handled){
                DragEventArgs drop=NewDragArgs(data,keys,allowed,view,viewPoint);
                drop.RoutedEvent=DragDrop.DropEvent;drop.Source=area;
                area.RaiseEvent(drop);
                outcome.Handled=drop.Handled;outcome.Effects=drop.Effects;
            }
        }catch(Exception ex){outcome.Error=ex;}
        return outcome;
    }
    static DropOutcome RaiseWindowDrop(MainWindow window,DataObject data){
        DropOutcome outcome=new DropOutcome();
        try{
            DragEventArgs drop=NewDragArgs(data,DragDropKeyStates.LeftMouseButton,DragDropEffects.Copy,window,new Point(0,0));
            drop.RoutedEvent=DragDrop.DropEvent;drop.Source=window;
            window.RaiseEvent(drop);
            outcome.Handled=drop.Handled;outcome.Effects=drop.Effects;
        }catch(Exception ex){outcome.Error=ex;}
        return outcome;
    }
    static DataObject FileDrop(string[] files){var data=new DataObject();data.SetData(DataFormats.FileDrop,files);return data;}
    static DataObject TextDrop(string text){var data=new DataObject();data.SetData(DataFormats.UnicodeText,text);return data;}
    // Visual point for a document line/column, in the TextView's own coordinates (the space
    // DragEventArgs.GetPosition returns). The line is scrolled into view and the point checked.
    static Point DropPoint(SourceEditor editor,int line,int column,int dx,bool atEnd){
        var view=editor.TextArea.TextView;
        editor.ScrollToLine(line);Pump();view.EnsureVisualLines();
        TextViewPosition position=new TextViewPosition(line,Math.Max(1,column));position.IsAtEndOfLine=atEnd;
        Point document=view.GetVisualPosition(position,VisualYPosition.LineMiddle);
        if(document.Y-view.ScrollOffset.Y<8||document.Y-view.ScrollOffset.Y>view.ActualHeight-8){
            editor.ScrollToVerticalOffset(Math.Max(0,document.Y-view.ActualHeight/2));Pump();view.EnsureVisualLines();
            document=view.GetVisualPosition(position,VisualYPosition.LineMiddle);
        }
        return new Point(document.X-view.ScrollOffset.X+dx,document.Y-view.ScrollOffset.Y);
    }
    static int HitOffset(SourceEditor editor,Point viewPoint){
        var view=editor.TextArea.TextView;
        TextViewPosition? hit=view.GetPositionFloor(viewPoint+view.ScrollOffset);
        return hit.HasValue?editor.Document.GetOffset(hit.Value.Location):-1;
    }
    static MainWindow OpenDocument(string path,bool edit=true){
        MainWindow window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        window.Show();
        Call(window,"LoadFileCore",path);
        Pump();
        window.SetMode(edit);
        Pump();
        return window;
    }
    static void SetRaw(MainWindow window,bool raw){
        CheckBox preview=Field<CheckBox>(window,"preview");
        preview.IsChecked=raw;
        Pump();
    }
    static void SafeClose(MainWindow window){
        if(window==null)return;
        try{
            Call(window,"FlushDocumentChanges");
            string current=Prop<string>(window,"text");
            SetField(window,"saved",current);
            SetField(window,"recoveredDocument",false);
            window.Close();
            Pump();
        }catch(Exception ex){Check("close window",false,ex.GetType().Name+": "+ex.Message);}
    }
    static void ResetEditor(MainWindow window,string text){
        window.SetText(text);
        Pump();
        if(!Field<bool>(window,"editing")){window.SetMode(true);Pump();}
    }
    static void WriteImage(string file,System.Windows.Media.Imaging.BitmapEncoder encoder){
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(10,10,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var visual=new System.Windows.Media.DrawingVisual();
        using(var context=visual.RenderOpen())context.DrawRectangle(System.Windows.Media.Brushes.SteelBlue,null,new Rect(0,0,10,10));
        bitmap.Render(visual);
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using(var stream=File.Create(file))encoder.Save(stream);
    }
    // Two files holding the same bytes: the copy rule reuses an identical picture instead of
    // adding -2, so the checks compare content the same way the product does.
    static bool SameBytes(string left,string right){
        try{
            byte[] one=File.ReadAllBytes(left),two=File.ReadAllBytes(right);
            if(one.Length!=two.Length)return false;
            for(int i=0;i<one.Length;i++)if(one[i]!=two[i])return false;
            return true;
        }catch(Exception){return false;}
    }
    // The product's shared picture folder (Documents\MDNote\images), read from the product's own
    // helper so the probe cannot drift from the folder a paste uses.
    static string TempPictureFolder(){
        MethodInfo method=typeof(MainWindow).GetMethod("PastedImageTempFolder",Members);
        if(method==null)throw new MissingMemberException("MainWindow PastedImageTempFolder");
        return (string)method.Invoke(null,new object[0]);
    }
    // Every picture the copy rule put into <docdir>\<docname> (FixtureDir\doc) is a copy this probe
    // made, so the folder starts empty for each case that counts files by name. The folder itself is
    // recreated on the next copy.
    static void ResetDocImageFolder(){
        ClearTempPictureFolder();
        try{
            if(String.IsNullOrEmpty(DocImageFolder)||!Directory.Exists(DocImageFolder))return;
            foreach(string file in Directory.GetFiles(DocImageFolder)){
                try{File.Delete(file);}catch(Exception){}
            }
            foreach(string folder in Directory.GetDirectories(DocImageFolder)){
                try{Directory.Delete(folder,true);}catch(Exception){}
            }
        }catch(Exception){}
    }
    // The shared picture folder is the probe's own (the product helper reads the override), so the
    // run never touches Documents\MDNote\images and starts from an empty one.
    static void ClearTempPictureFolder(){
        try{
            string temp=TempPictureFolder();
            if(!Directory.Exists(temp))return;
            foreach(string file in Directory.GetFiles(temp)){try{File.Delete(file);}catch(Exception){}}
        }catch(Exception){}
    }
    // The link text an insert writes for a picture staged in the shared folder (absolute path).
    static string StagedRef(string fileName){return EscapeRef(Path.Combine(TempPictureFolder(),fileName).Replace('\\','/'));}
    static void PrepareFixture(){
        OutDir=Path.GetFullPath(OutDir);
        MainWindow.ImageTempFolderOverride=Path.Combine(OutDir,"temp-images");
        Directory.CreateDirectory(MainWindow.ImageTempFolderOverride);
        ClearTempPictureFolder();
        FixtureDir=Path.Combine(OutDir,"drop-fixture");
        Directory.CreateDirectory(FixtureDir);
        Directory.CreateDirectory(Path.Combine(FixtureDir,"sub"));
        DocPath=Path.Combine(FixtureDir,"doc.md");
        SamePath=Path.Combine(FixtureDir,"그림 파일 (1).png");
        SubPath=Path.Combine(FixtureDir,"sub","inside.png");
        JpgPath=Path.Combine(FixtureDir,"photo.jpg");
        GifPath=Path.Combine(FixtureDir,"anim.gif");
        SvgPath=Path.Combine(FixtureDir,"icon.svg");
        PickPath=Path.Combine(FixtureDir,"pick.txt");
        TxtPath=Path.Combine(FixtureDir,"notes.txt");
        File.WriteAllText(DocPath,FixtureText,new UTF8Encoding(false));
        File.WriteAllText(SvgPath,SvgText,new UTF8Encoding(false));
        File.WriteAllText(PickPath,PickText,new UTF8Encoding(false));
        File.WriteAllText(TxtPath,TxtText,new UTF8Encoding(false));
        WriteImage(SamePath,new System.Windows.Media.Imaging.PngBitmapEncoder());
        WriteImage(SubPath,new System.Windows.Media.Imaging.PngBitmapEncoder());
        WriteImage(JpgPath,new System.Windows.Media.Imaging.JpegBitmapEncoder());
        WriteImage(GifPath,new System.Windows.Media.Imaging.GifBitmapEncoder());
        SameFull=Path.GetFullPath(SamePath);
        SameAlt="그림 파일 (1)";SameRef=EscapeRef("그림 파일 (1).png");
        SubAlt="inside";SubRef="sub/inside.png";
        JpgAlt="photo";JpgRef="photo.jpg";
        GifAlt="anim";GifRef="anim.gif";
        /* 2026-11-03 copy rule: doc.md lives in FixtureDir, so its own picture folder is
           FixtureDir\doc. A picture merely beside the .md or in FixtureDir\sub is NOT inside that
           folder, so an insert copies it into doc\ and links the copy relatively. */
        DocImageFolder=Path.Combine(FixtureDir,"doc");
        /* 2026-11-03 staging rule: an insert never touches <docname>\ ; the picture waits in the
           shared temp folder with an absolute link, and the save moves it (RunStageSave). */
        SameRefCopy=StagedRef(Path.GetFileName(SamePath));
        SubRefCopy=StagedRef("inside.png");
        JpgRefCopy=StagedRef("photo.jpg");
        GifRefCopy=StagedRef("anim.gif");
        OtherPath="D:\\pics\\tail.png";
        /* A picture from OUTSIDE the document folder: the new rule copies it into doc\ first (the
           document is <FixtureDir>\doc.md, so the copy goes to <FixtureDir>\doc\). */
        OutsideDir=Path.Combine(OutDir,"outside-pictures");
        Directory.CreateDirectory(OutsideDir);
        OutsidePath=Path.Combine(OutsideDir,"outside.png");
        /* Two different pictures that both want the name a.png: the second copy takes a-2. */
        ClashPathA=Path.Combine(Path.Combine(OutDir,"clash-a"),"a.png");
        ClashPathB=Path.Combine(Path.Combine(OutDir,"clash-b"),"a.png");
        Directory.CreateDirectory(Path.GetDirectoryName(ClashPathA));
        Directory.CreateDirectory(Path.GetDirectoryName(ClashPathB));
        WriteImage(OutsidePath,new System.Windows.Media.Imaging.PngBitmapEncoder());
        WriteImage(ClashPathA,new System.Windows.Media.Imaging.PngBitmapEncoder());
        WriteImage(ClashPathB,new System.Windows.Media.Imaging.JpegBitmapEncoder());
        /* The pictures the copy rule puts into doc\ are judged by their names, so every earlier copy
           is removed first (a folder from an earlier run would otherwise decide the -2 checks). */
        try{
            if(Directory.Exists(DocImageFolder))
                foreach(string stale in Directory.GetFiles(DocImageFolder)){
                    try{File.Delete(stale);}catch(Exception){}
                }
        }catch(Exception){}
        UnsavedRef=EscapeRef(SameFull.Replace('\\','/'));
        OutsideAlt="outside";
        OutsideRef=StagedRef("outside.png");
        string[] expected=new string[]{"# 제목 줄","","첫 문단 줄입니다.","둘째 문단 줄입니다.","","- 목록 항목 하나","- 목록 항목 둘","","| A | B |","|---|---|","| a1 | b1 |","| a2 | b2 |","","```text","code line one","code line two","```","","마지막 문단."};
        string[] actual=FixtureText.Split('\n');
        bool linesOk=actual.Length==expected.Length;
        int firstBad=0;
        for(int i=0;linesOk&&i<expected.Length;i++)if(actual[i]!=expected[i]){linesOk=false;firstBad=i+1;}
        Check("fixture line layout",linesOk,linesOk?"19 lines":"line "+firstBad+" is '"+Excerpt(firstBad>0&&firstBad<=actual.Length?actual[firstBad-1]:"?")+"'");
        Check("fixture image files exist",File.Exists(SamePath)&&File.Exists(SubPath)&&File.Exists(JpgPath)&&File.Exists(GifPath),SamePath);
    }
    static string BaseText=FixtureText;

    // One image-file drop through the real handlers. The point is computed from the live editor;
    // the expected insertion offset is the same TextView hit test the product uses.
    static void RunImageCase(MainWindow window,string label,string file,string markup,int line,int column,int dx,bool atEnd,string prefix,int pipes){
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            string beforeLine=LineText(editor,line);
            int beforePipes=pipes>=0?CountChar(beforeLine,'|'):-1;
            Point point=DropPoint(editor,line,column,dx,atEnd);
            int at=HitOffset(editor,point);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{file}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            string afterLine=LineText(editor,line);
            int insertedAt=InsertStart(before,after);
            string inserted=InsertedText(before,after);
            int caret=editor.CaretIndex,selection=editor.SelectionLength;
            Check(label+" handler",outcome.Error==null&&outcome.Handled&&dispatchOk,outcome.Error==null?("handled="+outcome.Handled):("error="+outcome.Error.GetType().Name+": "+outcome.Error.Message));
            int hitLine=LineOf(editor,at);
            bool prefixOk=prefix==null||afterLine.StartsWith(prefix,StringComparison.Ordinal);
            bool pipesOk=pipes<0||CountChar(afterLine,'|')==beforePipes;
            bool placed=hitLine==line&&insertedAt==at&&inserted==markup&&prefixOk&&pipesOk;
            Check(label+" inserts the documented image at the drop point",placed,"at="+at+" line="+hitLine+", inserted='"+Excerpt(inserted)+"', before='"+Excerpt(beforeLine)+"', after='"+Excerpt(afterLine)+"'");
            if(atEnd)Check(label+" drop point is the document end",at==before.Length,"at="+at+", length="+before.Length);
            Check(label+" caret ends after the image",caret==at+markup.Length&&selection==0,"caret="+caret+" selection="+selection+" want="+(at+markup.Length));
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the document",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    // Read mode is the default state of a Markdown document: an existing image dropped there is
    // still the editor's insert. It turns editing on through the same entry point as Ctrl+E and
    // writes at the point the user aimed at, so the window never opens the PNG as a text document.
    static void ReadModeImageCase(MainWindow window,string label,string file,string markup,int line,int column){
        try{
            SourceEditor editor=Source(window);
            Check(label+" starts in read mode",!Field<bool>(window,"editing")&&editor.IsReadOnly,"editing="+Field<bool>(window,"editing")+", readOnly="+editor.IsReadOnly);
            string before=editor.Document.Text;
            Point point=DropPoint(editor,line,column,0,false);
            int at=HitOffset(editor,point);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{file}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            int insertedAt=InsertStart(before,after);
            string inserted=InsertedText(before,after);
            int caret=editor.CaretIndex,selection=editor.SelectionLength;
            Check(label+" handler",outcome.Error==null&&outcome.Handled&&outcome.Effects==DragDropEffects.Copy&&dispatchOk,outcome.Error==null?("handled="+outcome.Handled+", effects="+outcome.Effects):("error="+outcome.Error.GetType().Name+": "+outcome.Error.Message));
            Check(label+" turns editing on",Field<bool>(window,"editing")&&!editor.IsReadOnly,"editing="+Field<bool>(window,"editing")+", readOnly="+editor.IsReadOnly);
            bool placed=LineOf(editor,at)==line&&insertedAt==at&&inserted==markup;
            Check(label+" inserts the image at the drop point",placed,"at="+at+" line="+LineOf(editor,at)+", inserted='"+Excerpt(inserted)+"', before='"+Excerpt(LineText(editor,line))+"'");
            Check(label+" caret ends after the image",caret==at+markup.Length&&selection==0,"caret="+caret+" selection="+selection+" want="+(at+markup.Length));
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the document",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    // The empty space under the last line hits no text line of its own; the editor still owns the
    // drop (it appends at the document end) instead of letting the window open the PNG.
    static void BelowTextImageCase(MainWindow window){
        string label="read mode image below the text";
        try{
            SourceEditor editor=Source(window);
            Check(label+" starts in read mode",!Field<bool>(window,"editing"),"editing="+Field<bool>(window,"editing"));
            string before=editor.Document.Text;
            string markup=Markup(SameAlt,SameRefCopy);
            var view=editor.TextArea.TextView;
            var last=view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(editor.Document.LineCount));
            Point point=new Point(40,last.VisualTop+last.Height+6-view.ScrollOffset.Y);
            Check(label+" point is in the empty space under the last line",point.Y>=0&&point.Y<view.ActualHeight&&!view.GetPositionFloor(point+view.ScrollOffset).HasValue,"y="+point.Y+" height="+view.ActualHeight);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{SamePath}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            Check(label+" handler",outcome.Error==null&&outcome.Handled&&dispatchOk,outcome.Error==null?("handled="+outcome.Handled):("error="+outcome.Error.GetType().Name+": "+outcome.Error.Message));
            Check(label+" appends the image at the document end",after==before+markup,"inserted='"+Excerpt(InsertedText(before,after))+"'");
            Check(label+" caret ends after the image",editor.CaretIndex==after.Length&&editor.SelectionLength==0,"caret="+editor.CaretIndex+" want="+after.Length);
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the document",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void TableImageCase(MainWindow window,string mode){
        string label=mode+" image table cell";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            int line=11;
            string row=LineText(editor,line);
            int lineOffset=editor.Document.GetLineByNumber(line).Offset;
            int pipe1=row.IndexOf('|',1),pipe2=row.IndexOf('|',pipe1+1);
            Point point=DropPoint(editor,line,8,0,false);
            int at=HitOffset(editor,point);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{SamePath}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            string afterRow=LineText(editor,line);
            int insertedAt=InsertStart(before,after);
            string inserted=InsertedText(before,after);
            int caret=editor.CaretIndex,selection=editor.SelectionLength;
            string markup=Markup(SameAlt,SameRefCopy);
            bool inCell=pipe1>0&&pipe2>pipe1&&at>lineOffset+pipe1&&at<lineOffset+pipe2;
            Check(label+" handler",outcome.Error==null&&outcome.Handled&&dispatchOk,outcome.Error==null?("handled="+outcome.Handled):("error="+outcome.Error.GetType().Name+": "+outcome.Error.Message));
            bool placed=LineOf(editor,at)==line&&inCell&&insertedAt==at&&inserted==markup&&CountChar(afterRow,'|')==CountChar(row,'|')&&afterRow.StartsWith("|",StringComparison.Ordinal)&&afterRow.EndsWith("|",StringComparison.Ordinal);
            Check(label+" keeps the row's cell count and inserts the image in the cell",placed,"at="+at+" inCell="+inCell+", pipes="+CountChar(row,'|')+"->"+CountChar(afterRow,'|')+", after='"+Excerpt(afterRow)+"'");
            Check(label+" caret ends after the image",caret==at+markup.Length&&selection==0,"caret="+caret+" selection="+selection+" want="+(at+markup.Length));
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the document",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    // An image dropped inside a fenced code block is refused: a token written there is literal
    // text, not a picture, so the source consumes the drop and writes nothing.
    static void CodeImageCase(MainWindow window,string mode){
        string label=mode+" image code block";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            string openBefore=LineText(editor,14),closeBefore=LineText(editor,17);
            int linesBefore=editor.Document.LineCount;
            int caretBefore=editor.CaretIndex,selectionBefore=editor.SelectionLength;
            Point point=DropPoint(editor,15,6,0,false);
            int at=HitOffset(editor,point);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{SamePath}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            int caret=editor.CaretIndex,selection=editor.SelectionLength;
            Check(label+" handler",outcome.Error==null&&outcome.Handled&&dispatchOk,outcome.Error==null?("handled="+outcome.Handled):("error="+outcome.Error.GetType().Name+": "+outcome.Error.Message));
            bool inFence=LineOf(editor,at)==15&&LineText(editor,15).StartsWith("code ",StringComparison.Ordinal);
            bool refused=outcome.PreviewHandled&&inFence&&after==before&&LineText(editor,14)==openBefore&&LineText(editor,17)==closeBefore&&editor.Document.LineCount==linesBefore;
            Check(label+" refuses the image inside the fence and keeps the text",refused,"previewHandled="+outcome.PreviewHandled+", inFence="+inFence+", changed="+(after!=before)+", fences="+(LineText(editor,14)==openBefore&&LineText(editor,17)==closeBefore)+", lines="+editor.Document.LineCount+" want="+linesBefore);
            Check(label+" caret and selection stay where they were",caret==caretBefore&&selection==selectionBefore,"caret="+caret+" selection="+selection+" want="+caretBefore+"/"+selectionBefore);
            if(after!=before)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void MultipleImagesCase(MainWindow window,string mode){
        string label=mode+" multiple images";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            string want=Markup(SameAlt,SameRefCopy)+"\n"+Markup(SubAlt,SubRefCopy);
            Point point=DropPoint(editor,3,3,0,false);
            int at=HitOffset(editor,point);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{SamePath,SubPath}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            int insertedAt=InsertStart(before,after);
            string inserted=InsertedText(before,after);
            int caret=editor.CaretIndex,selection=editor.SelectionLength;
            Check(label+" handler",outcome.Error==null&&outcome.Handled&&dispatchOk,outcome.Error==null?("handled="+outcome.Handled):("error="+outcome.Error.GetType().Name+": "+outcome.Error.Message));
            bool placed=insertedAt==at&&inserted==want&&CountChar(after,'\n')==CountChar(before,'\n')+1;
            Check(label+" insert one image per line in file order",placed,"inserted='"+Excerpt(inserted)+"'");
            Check(label+" caret ends after the last image",caret==at+want.Length&&selection==0,"caret="+caret+" selection="+selection+" want="+(at+want.Length));
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores both images",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    // One non-existent image-file drop through the real handlers: the source surface must not
    // consume it as an image insert, so the text stays unchanged.
    static void MissingImageCase(MainWindow window,string label,string file,string checkText){
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            Point point=DropPoint(editor,3,3,0,false);
            int at=HitOffset(editor,point);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{file}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            DrainDispatch(label);
            string after=editor.Document.Text;
            string inserted=InsertedText(before,after);
            bool refused=outcome.Error==null&&!outcome.PreviewHandled&&after==before;
            Check(label+" "+checkText,refused,"file='"+file+"', exists="+File.Exists(file)+", previewHandled="+outcome.PreviewHandled+", handled="+outcome.Handled+", inserted='"+Excerpt(inserted)+"'");
            if(!refused){Call(window,"Undo");Pump();if(editor.Document.Text!=before)ResetEditor(window,BaseText);}
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void MissingTextPreviewCase(MainWindow window,string mode){
        string label=mode+" missing text file";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            string missing=Path.Combine(FixtureDir,"missing-note.txt");
            Point point=DropPoint(editor,3,3,0,false);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{missing}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            DrainDispatch(label);
            Check(label+" non-existent non-image file is not inserted by the editor",outcome.Error==null&&!outcome.PreviewHandled&&editor.Document.Text==before,"previewHandled="+outcome.PreviewHandled+", exists="+File.Exists(missing)+", changed="+(editor.Document.Text!=before));
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void UnsupportedPreviewCase(MainWindow window,string mode){
        string label=mode+" unsupported file";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            Point point=DropPoint(editor,3,3,0,false);
            DropOutcome outcome=RaiseDrop(window,FileDrop(new string[]{SvgPath}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            DrainDispatch(label);
            bool declined=outcome.Error==null&&!outcome.Handled&&editor.Document.Text==before;
            Check(label+" unsupported image type is not inserted by the editor",declined,"handled="+outcome.Handled+", changed="+(editor.Document.Text!=before));
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void TextCase(MainWindow window,string label,string value,int line,int column){
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            Point point=DropPoint(editor,line,column,0,false);
            int at=HitOffset(editor,point);
            DropOutcome outcome=RaiseDrop(window,TextDrop(value),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,true);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            int insertedAt=InsertStart(before,after);
            string inserted=InsertedText(before,after);
            int caret=editor.CaretIndex,selection=editor.SelectionLength;
            bool placed=outcome.Error==null&&LineOf(editor,at)==line&&insertedAt==at&&inserted==value;
            Check(label+" inserts the dragged text at the drop point",placed,"at="+at+" line="+LineOf(editor,at)+", inserted='"+Excerpt(inserted)+"', handled="+outcome.Handled+(outcome.Error==null?"":" error="+outcome.Error.GetType().Name)+", dispatcher="+dispatchOk);
            Check(label+" caret ends after the inserted text",caret==at+value.Length,"caret="+caret+" selection="+selection+" want="+(at+value.Length));
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the document",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static DataObject MoveData(SourceEditor editor,string selected){
        var data=new DataObject();
        data.SetData(DataFormats.UnicodeText,selected);
        data.SetData(SourceEditor.DragTokenFormat,editor.DragSourceToken);
        return data;
    }
    static void MoveCase(MainWindow window,string mode){
        string label=mode+" internal move";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            int start=before.IndexOf("둘째",StringComparison.Ordinal);
            int length=2;
            string selected=before.Substring(start,length);
            Point point=DropPoint(editor,3,2,0,false);
            int at=HitOffset(editor,point);
            editor.Select(start,length);Pump();
            editor.NoteDragSource();
            DropOutcome outcome=RaiseDrop(window,MoveData(editor,selected),point,DragDropEffects.Move|DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            bool dispatchOk=DrainDispatch(label);
            int target=at>start?at-length:at;
            string expected=before.Remove(start,length).Insert(target,selected);
            string after=editor.Document.Text;
            bool moved=outcome.Error==null&&outcome.Handled&&dispatchOk&&after==expected&&editor.SelectionStart==target&&editor.SelectionLength==length&&editor.CaretIndex==target+length;
            Check(label+" moves the dragged selection as one action",moved,"at="+at+" target="+target+", caret="+editor.CaretIndex+" selection="+editor.SelectionLength+", handled="+outcome.Handled+", after='"+Excerpt(after)+"'");
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the moved text",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(undone){
                Call(window,"Redo");Pump();
                bool redone=editor.Document.Text==expected;
                Check(label+" one redo moves it again",redone,redone?"reapplied":"text='"+Excerpt(editor.Document.Text)+"'");
                Call(window,"Undo");Pump();
            }
            if(editor.Document.Text!=before)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void InsideSelectionCase(MainWindow window,string mode){
        string label=mode+" inside-selection drop";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            int start=before.IndexOf("둘째",StringComparison.Ordinal);
            int length=2;
            int lineStart=editor.Document.GetLineByNumber(4).Offset;
            Point point=DropPoint(editor,4,start-lineStart+2,0,false);
            editor.Select(start,length);Pump();
            editor.NoteDragSource();
            string selected=before.Substring(start,length);
            DropOutcome outcome=RaiseDrop(window,MoveData(editor,selected),point,DragDropEffects.Move|DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            DrainDispatch(label);
            bool unchanged=outcome.Error==null&&outcome.Handled&&outcome.Effects==DragDropEffects.None&&editor.Document.Text==before;
            Check(label+" dropping inside the dragged block moves nothing",unchanged,"handled="+outcome.Handled+", effects="+outcome.Effects+", text='"+Excerpt(editor.Document.Text)+"'");
            if(editor.Document.Text!=before)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void CopyCase(MainWindow window,string mode){
        string label=mode+" ctrl-drag copy";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            int start=before.IndexOf("둘째",StringComparison.Ordinal);
            int length=2;
            string selected=before.Substring(start,length);
            Point point=DropPoint(editor,3,2,0,false);
            int at=HitOffset(editor,point);
            editor.Select(start,length);Pump();
            editor.NoteDragSource();
            DropOutcome outcome=RaiseDrop(window,MoveData(editor,selected),point,DragDropEffects.Move|DragDropEffects.Copy,DragDropKeyStates.ControlKey|DragDropKeyStates.LeftMouseButton,true);
            Pump();
            DrainDispatch(label);
            string after=editor.Document.Text;
            int insertedAt=InsertStart(before,after);
            string inserted=InsertedText(before,after);
            int caret=editor.CaretIndex,selection=editor.SelectionLength;
            bool copied=outcome.Error==null&&LineOf(editor,at)==3&&insertedAt==at&&inserted==selected&&after.Length==before.Length+length;
            Check(label+" Ctrl+drag copies the selection at the drop point",copied,"at="+at+" line="+LineOf(editor,at)+", inserted='"+Excerpt(inserted)+"', after='"+Excerpt(after)+"'");
            Check(label+" caret ends after the copied text",caret==at+selected.Length,"caret="+caret+" selection="+selection+" want="+(at+selected.Length));
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the copy",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            editor.ClearDragSource();
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void PathRuleChecks(){
        string decoded="";
        try{decoded=Uri.UnescapeDataString(SameRef);}catch(Exception){}
        bool resolves=decoded.Length>0&&String.Equals(Path.GetFullPath(Path.Combine(FixtureDir,decoded)),SameFull,StringComparison.OrdinalIgnoreCase);
        Check("image reference encodes spaces/parentheses, keeps Korean, and resolves",SameRef.IndexOf("%20",StringComparison.Ordinal)>=0&&SameRef.IndexOf("%28",StringComparison.Ordinal)>=0&&SameRef.IndexOf("%29",StringComparison.Ordinal)>=0&&SameRef.IndexOf("그림",StringComparison.Ordinal)>=0&&SameRef.IndexOf(" ",StringComparison.Ordinal)<0&&resolves,"ref='"+SameRef+"', resolves="+resolves);
        string decodedSub="";
        try{decodedSub=Uri.UnescapeDataString(SubRef);}catch(Exception){}
        bool subResolves=decodedSub.Length>0&&String.Equals(Path.GetFullPath(Path.Combine(FixtureDir,decodedSub)),Path.GetFullPath(SubPath),StringComparison.OrdinalIgnoreCase);
        Check("sub-folder image reference resolves next to the document",subResolves,"ref='"+SubRef+"'");
    }
    static void RunMode(bool raw){
        string mode=raw?"raw":"live";
        MainWindow window=null;
        BaseText=FixtureText;
        try{
            window=OpenDocument(DocPath);
            bool attached=Prop<bool>(window,"LivePreviewAttached");
            Check(mode+" mode window loaded the fixture",window.OpenDocumentCount==1&&Text(window)==FixtureText,"documents="+window.OpenDocumentCount+", editing="+Field<bool>(window,"editing")+", attached="+attached);
            if(raw)SetRaw(window,true);else if(!attached)SetRaw(window,false);
            bool nowAttached=Prop<bool>(window,"LivePreviewAttached");
            Check(mode+" surface state",raw?!nowAttached:nowAttached,"attached="+nowAttached+", editing="+Field<bool>(window,"editing"));
            SourceEditor editor=Source(window);
            int lastLength=LineText(editor,19).Length;
            RunImageCase(window,mode+" image paragraph",SamePath,Markup(SameAlt,SameRefCopy),3,3,0,false,null,-1);
            RunImageCase(window,mode+" image list item",SamePath,Markup(SameAlt,SameRefCopy),6,5,0,false,"- ",-1);
            RunImageCase(window,mode+" image heading",SamePath,Markup(SameAlt,SameRefCopy),1,4,0,false,"# ",-1);
            TableImageCase(window,mode);
            CodeImageCase(window,mode);
            RunImageCase(window,mode+" image document end",SamePath,Markup(SameAlt,SameRefCopy),19,lastLength+1,120,true,null,-1);
            MultipleImagesCase(window,mode);
            if(!raw){
                RunImageCase(window,mode+" image beside the document",SamePath,Markup(SameAlt,SameRefCopy),3,3,0,false,null,-1);
                RunImageCase(window,mode+" image sub folder",SubPath,Markup(SubAlt,SubRefCopy),3,3,0,false,null,-1);
                // The other-drive form is a path rule, not an insert: D:\pics\tail.png is not on disk
                // and the probe must not create it, so check what the shared reference helper writes.
                string otherReference=MarkdownCommands.ImageReference(DocPath,OtherPath);
                Check(mode+" image other drive path stays absolute",otherReference=="D:/pics/tail.png","reference='"+otherReference+"', document='"+DocPath+"'");
                RunImageCase(window,mode+" image jpg",JpgPath,Markup(JpgAlt,JpgRefCopy),3,3,0,false,null,-1);
                RunImageCase(window,mode+" image gif",GifPath,Markup(GifAlt,GifRefCopy),4,3,0,false,null,-1);
                MissingImageCase(window,mode+" missing image",Path.Combine(FixtureDir,"missing image.png"),"non-existent image file does not insert broken syntax");
                MissingImageCase(window,mode+" image other drive",OtherPath,"missing file inserts nothing");
                MissingTextPreviewCase(window,mode);
                UnsupportedPreviewCase(window,mode);
                PathRuleChecks();
            }
            TextCase(window,mode+" text drag","붙여 넣은 글자",3,3);
            TextCase(window,mode+" markdown text drag","**굵게**",4,3);
            MoveCase(window,mode);
            InsideSelectionCase(window,mode);
            CopyCase(window,mode);
        }catch(Exception ex){Check(mode+" mode run",false,ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(window);}
    }
    // The state a .md opens in (no editing) must answer the same image drops as edit mode: the
    // editor still consumes an existing image, and only a name that is gone keeps the window's
    // open-as-document answer, so a PNG is never handed to the text decoder.
    static void RunReadMode(){
        MainWindow window=null;
        BaseText=FixtureText;
        try{
            window=OpenDocument(DocPath,false);
            Check("read mode window opened out of editing",window.OpenDocumentCount==1&&Text(window)==FixtureText&&!Field<bool>(window,"editing"),"documents="+window.OpenDocumentCount+", editing="+Field<bool>(window,"editing")+", text='"+Excerpt(Text(window))+"'");
            Check("read mode keeps the live surface attached and read-only",Prop<bool>(window,"LivePreviewAttached")&&Source(window).IsReadOnly,"attached="+Prop<bool>(window,"LivePreviewAttached")+", readOnly="+Source(window).IsReadOnly);
            ReadModeImageCase(window,"read mode image",SamePath,Markup(SameAlt,SameRefCopy),3,3);
            window.SetMode(false);Pump();
            int notices=0;string notice=null;
            MainWindow.ImageDropNotice=delegate(string message){notices++;notice=message;};
            MissingImageCase(window,"read mode missing image",Path.Combine(FixtureDir,"missing image.png"),"non-existent image file does not insert broken syntax");
            Check("read mode missing image stays out of editing and shows no notice",!Field<bool>(window,"editing")&&notices==0&&Text(window)==FixtureText,"editing="+Field<bool>(window,"editing")+", notices="+notices+", notice='"+Excerpt(notice)+"', text='"+Excerpt(Text(window))+"'");
            window.Height=1100;Pump();
            BelowTextImageCase(window);
        }catch(Exception ex){Check("read mode run",false,ex.GetType().Name+": "+ex.Message);}
        finally{MainWindow.ImageDropNotice=null;SafeClose(window);}
    }
    static void RunTxtMode(){
        MainWindow window=null;int notices=0;string notice=null;
        try{
            window=OpenDocument(TxtPath);
            Check("txt document loaded as text",Prop<bool>(window,"PlainText")&&Text(window)==TxtText,"plain="+Prop<bool>(window,"PlainText")+", text='"+Excerpt(Text(window))+"'");
            MainWindow.ImageDropNotice=delegate(string message){notices++;notice=message;};
            string expected=(string)Call(window,"T","이미지는 MD 문서에서만 넣을 수 있습니다.");
            Point live=DropPoint(Source(window),1,2,0,false);
            DropOutcome first=RaiseDrop(window,FileDrop(new string[]{SamePath}),live,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            Check("txt source image drop shows the notice and changes nothing",first.Error==null&&first.Handled&&notices==1&&notice==expected&&Text(window)==TxtText,"handled="+first.Handled+", notices="+notices+", notice='"+Excerpt(notice)+"', text='"+Excerpt(Text(window))+"'");
            SetRaw(window,true);
            Point raw=DropPoint(Source(window),1,2,0,false);
            int beforeRaw=notices;
            DropOutcome second=RaiseDrop(window,FileDrop(new string[]{SamePath}),raw,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            Check("txt raw-mode image drop shows the notice and changes nothing",second.Error==null&&second.Handled&&notices==beforeRaw+1&&Text(window)==TxtText,"handled="+second.Handled+", notices="+notices+", text='"+Excerpt(Text(window))+"'");
            int beforeWindow=notices;
            DropOutcome third=RaiseWindowDrop(window,FileDrop(new string[]{SamePath}));
            Pump();
            Check("txt window-level image drop shows the notice and opens no document",third.Error==null&&third.Handled&&notices==beforeWindow+1&&window.OpenDocumentCount==1&&Text(window)==TxtText&&String.Equals(Field<string>(window,"path"),Path.GetFullPath(TxtPath),StringComparison.OrdinalIgnoreCase),"handled="+third.Handled+", notices="+notices+", documents="+window.OpenDocumentCount+", path='"+Field<string>(window,"path")+"'");
        }catch(Exception ex){Check("txt mode run",false,ex.GetType().Name+": "+ex.Message);}
        finally{MainWindow.ImageDropNotice=null;SafeClose(window);}
    }
    // The window's own Drop handler (status bar, tab strip, ribbon, margins): an all-image drop on
    // a Markdown document lands at the caret as one undo step, read mode included, and the PNG is
    // never handed to LoadFile (no document opens and the text decoder is never asked to read it).
    static void WindowImageCase(MainWindow window,string label,string file,string markup,bool readMode){
        try{
            if(readMode){window.SetMode(false);Pump();}
            SourceEditor editor=Source(window);
            bool modeOk=readMode?(!Field<bool>(window,"editing")&&editor.IsReadOnly):(Field<bool>(window,"editing")&&!editor.IsReadOnly);
            Check(label+" starts in "+(readMode?"read":"edit")+" mode",modeOk,"editing="+Field<bool>(window,"editing")+", readOnly="+editor.IsReadOnly);
            string before=editor.Document.Text;
            int caret=editor.Document.GetLineByNumber(4).Offset;
            editor.Select(caret,0);Pump();
            int documents=window.OpenDocumentCount;string path=Field<string>(window,"path");int windows=System.Windows.Application.Current.Windows.Count;
            DropOutcome outcome=RaiseWindowDrop(window,FileDrop(new string[]{file}));
            Pump();
            bool dispatchOk=DrainDispatch(label);
            string after=editor.Document.Text;
            bool inserted=outcome.Error==null&&outcome.Handled&&dispatchOk&&after==before.Insert(caret,markup);
            Check(label+" inserts the image at the caret",inserted,"handled="+(outcome.Error==null?outcome.Handled.ToString():"error="+outcome.Error.GetType().Name+": "+outcome.Error.Message)+", caret="+caret+", inserted='"+Excerpt(InsertedText(before,after))+"'");
            Check(label+" turns editing on",Field<bool>(window,"editing")&&!editor.IsReadOnly,"editing="+Field<bool>(window,"editing")+", readOnly="+editor.IsReadOnly);
            Check(label+" caret ends after the image",editor.CaretIndex==caret+markup.Length&&editor.SelectionLength==0,"caret="+editor.CaretIndex+" selection="+editor.SelectionLength+" want="+(caret+markup.Length));
            bool noOpen=dispatchOk&&window.OpenDocumentCount==documents&&String.Equals(Field<string>(window,"path"),path,StringComparison.OrdinalIgnoreCase)&&System.Windows.Application.Current.Windows.Count==windows;
            Check(label+" opens no document and never reaches LoadFile",noOpen,"documents="+window.OpenDocumentCount+", was "+documents+", path='"+Field<string>(window,"path")+"', windows="+System.Windows.Application.Current.Windows.Count+", was "+windows+", dispatcher="+dispatchOk);
            Call(window,"Undo");Pump();
            bool undone=editor.Document.Text==before;
            Check(label+" one undo restores the document",undone,undone?"restored":"text='"+Excerpt(editor.Document.Text)+"'");
            if(!undone)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    // A name that is gone inserts nothing, but the all-image drop is still consumed: LoadFile is
    // never asked to open the PNG, so no file-not-found or decode box can follow it.
    static void WindowMissingImageCase(MainWindow window){
        string label="window image drop missing file";
        try{
            SourceEditor editor=Source(window);
            string before=editor.Document.Text;
            int documents=window.OpenDocumentCount;string path=Field<string>(window,"path");int windows=System.Windows.Application.Current.Windows.Count;
            DropOutcome outcome=RaiseWindowDrop(window,FileDrop(new string[]{Path.Combine(FixtureDir,"missing window image.png")}));
            Pump();
            bool dispatchOk=DrainDispatch(label);
            bool consumed=outcome.Error==null&&outcome.Handled&&dispatchOk&&editor.Document.Text==before&&window.OpenDocumentCount==documents&&String.Equals(Field<string>(window,"path"),path,StringComparison.OrdinalIgnoreCase)&&System.Windows.Application.Current.Windows.Count==windows;
            Check(label+" is consumed without opening a document",consumed,"handled="+(outcome.Error==null?outcome.Handled.ToString():"error="+outcome.Error.GetType().Name+": "+outcome.Error.Message)+", documents="+window.OpenDocumentCount+", path='"+Field<string>(window,"path")+"', text='"+Excerpt(editor.Document.Text)+"'");
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);}
    }
    /* The copy rule (user, 2026-11-03, narrowed the same day): only a picture already inside the
       document's own picture folder (<docdir>\<docname>, subfolders included) is linked where it is;
       a picture beside the .md or in another subfolder of the document's directory is copied into
       <docdir>\<docname> and linked relatively. All of this runs through the window's own insert path
       (InsertImageFilesAtCaret), the one the drop, the Ctrl+V file paste and the image button share. */
    static void InsertImageFileCopyCase(MainWindow window){
        string label="image file copy";
        try{
            SourceEditor editor=Source(window);
            string docFolder=Path.Combine(FixtureDir,"doc");
            string stage=TempPictureFolder();
            string destination=Path.Combine(stage,"outside.png");
            string markup=Markup(OutsideAlt,OutsideRef);
            Check(label+" starts without the copy",!File.Exists(destination),"destination='"+destination+"'");
            string before=editor.Document.Text;
            int caret=editor.Document.GetLineByNumber(3).Offset;
            editor.Select(caret,0);Pump();
            object handled=Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{OutsidePath}});
            Pump();
            string after=editor.Document.Text;
            bool inserted=after==before.Insert(caret,markup);
            Check(label+" inserts the staged copy's absolute link",inserted,"handled="+handled+", caret="+caret+", inserted='"+Excerpt(InsertedText(before,after))+"'");
            Check(label+" staged the picture in the shared folder and left <docname>\\ alone",File.Exists(destination)&&SameBytes(OutsidePath,destination)&&!File.Exists(Path.Combine(docFolder,"outside.png")),"destination='"+destination+"', exists="+File.Exists(destination)+", same="+SameBytes(OutsidePath,destination)+", docFolderCopy="+File.Exists(Path.Combine(docFolder,"outside.png")));
            Check(label+" keeps the original file",File.Exists(OutsidePath),"original='"+OutsidePath+"'");
            Call(window,"Undo");Pump();
            Check(label+" one undo restores the document",editor.Document.Text==before,Excerpt(editor.Document.Text));
            if(editor.Document.Text!=before)ResetEditor(window,BaseText);
            /* A picture beside the .md (the document's directory, but outside its own picture folder)
               is copied too: that was the "no copy" case before the rule was narrowed. */
            string besideCopy=Path.Combine(stage,Path.GetFileName(SamePath));
            string beforeBeside=editor.Document.Text;
            int caretBeside=editor.Document.GetLineByNumber(4).Offset;
            editor.Select(caretBeside,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{SamePath}});
            Pump();
            string afterBeside=editor.Document.Text;
            Check(label+" a picture beside the .md is staged and linked absolutely",afterBeside==beforeBeside.Insert(caretBeside,Markup(SameAlt,SameRefCopy))&&File.Exists(besideCopy)&&SameBytes(SamePath,besideCopy),"inserted='"+Excerpt(InsertedText(beforeBeside,afterBeside))+"', copy="+File.Exists(besideCopy));
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=beforeBeside)ResetEditor(window,BaseText);
            /* A picture in another subfolder of the document's directory is copied as well. */
            string subCopy=Path.Combine(stage,"inside.png");
            string beforeSub=editor.Document.Text;
            int caretSub=editor.Document.GetLineByNumber(5).Offset;
            editor.Select(caretSub,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{SubPath}});
            Pump();
            string afterSub=editor.Document.Text;
            Check(label+" a picture in another subfolder is staged and linked absolutely",afterSub==beforeSub.Insert(caretSub,Markup(SubAlt,SubRefCopy))&&File.Exists(subCopy)&&SameBytes(SubPath,subCopy),"inserted='"+Excerpt(InsertedText(beforeSub,afterSub))+"', copy="+File.Exists(subCopy));
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=beforeSub)ResetEditor(window,BaseText);
            /* Already inside the document's own picture folder: linked where it is, nothing copied. */
            Directory.CreateDirectory(docFolder);
            string insideTarget=Path.Combine(docFolder,"inside-keep.png");
            File.Copy(OutsidePath,insideTarget,true);
            string insideMarkup=Markup("inside-keep",EscapeRef("doc/inside-keep.png"));
            string beforeInside=editor.Document.Text;
            int caretInside=editor.Document.GetLineByNumber(4).Offset;
            editor.Select(caretInside,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{insideTarget}});
            Pump();
            string afterInside=editor.Document.Text;
            Check(label+" links a picture inside its own picture folder without copying",afterInside==beforeInside.Insert(caretInside,insideMarkup)&&File.Exists(insideTarget)&&!File.Exists(Path.Combine(docFolder,"inside-keep-2.png")),"inserted='"+Excerpt(InsertedText(beforeInside,afterInside))+"'");
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=beforeInside)ResetEditor(window,BaseText);
            /* A subfolder of the document's own picture folder counts as inside too. */
            string deepFolder=Path.Combine(docFolder,"deep");
            Directory.CreateDirectory(deepFolder);
            string deepTarget=Path.Combine(deepFolder,"deep.png");
            File.Copy(OutsidePath,deepTarget,true);
            string deepMarkup=Markup("deep",EscapeRef("doc/deep/deep.png"));
            string beforeDeep=editor.Document.Text;
            int caretDeep=editor.Document.GetLineByNumber(5).Offset;
            editor.Select(caretDeep,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{deepTarget}});
            Pump();
            string afterDeep=editor.Document.Text;
            Check(label+" a subfolder of the picture folder is also left in place",afterDeep==beforeDeep.Insert(caretDeep,deepMarkup)&&File.Exists(deepTarget)&&!File.Exists(Path.Combine(docFolder,"deep-2.png")),"inserted='"+Excerpt(InsertedText(beforeDeep,afterDeep))+"'");
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=beforeDeep)ResetEditor(window,BaseText);
            /* The image button's file dialog goes through InsertLiveImageFile, which is the same
               copy rule (the dialog itself cannot be driven from a probe). */
            string buttonSource=Path.Combine(OutsideDir,"button.png");
            File.Delete(buttonSource);
            File.Copy(OutsidePath,buttonSource);
            string buttonCopy=Path.Combine(stage,"button.png");
            string beforeButton=editor.Document.Text;
            int caretButton=editor.Document.GetLineByNumber(6).Offset;
            editor.Select(caretButton,0);Pump();
            bool buttonInserted=(bool)Call(window,"InsertLiveImageFile",new object[]{buttonSource});
            Pump();
            string afterButton=editor.Document.Text;
            Check(label+" the image button copies an outside file too",buttonInserted&&afterButton==beforeButton.Insert(caretButton,Markup("button",StagedRef("button.png")))&&File.Exists(buttonCopy)&&SameBytes(buttonSource,buttonCopy),"inserted="+buttonInserted+", inserted='"+Excerpt(InsertedText(beforeButton,afterButton))+"', copy="+File.Exists(buttonCopy));
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=beforeButton)ResetEditor(window,BaseText);
            /* A different picture with the same name takes -2. */
            string clash=Path.Combine(stage,"a-2.png");
            File.Delete(Path.Combine(stage,"a.png"));File.Delete(clash);
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{ClashPathA}});
            Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{ClashPathB}});
            Pump();
            Check(label+" a different picture with the same name takes -2",File.Exists(Path.Combine(stage,"a.png"))&&File.Exists(clash)&&SameBytes(ClashPathA,Path.Combine(stage,"a.png"))&&SameBytes(ClashPathB,clash)&&!SameBytes(Path.Combine(stage,"a.png"),clash),"a='"+Path.Combine(stage,"a.png")+"' is A="+SameBytes(ClashPathA,Path.Combine(stage,"a.png"))+", a-2='"+clash+"' is B="+SameBytes(ClashPathB,clash));
            Call(window,"Undo");Pump();Call(window,"Undo");Pump();
            /* The same picture again under the same name reuses the file instead of adding -3. */
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{OutsidePath}});
            Pump();
            Check(label+" a second copy of the same picture is reused",SameBytes(OutsidePath,destination)&&!File.Exists(Path.Combine(stage,"outside-2.png")),"outside-2 exists="+File.Exists(Path.Combine(stage,"outside-2.png")));
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=before)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    /* Explorer's Ctrl+V of copied picture files: the clipboard holds a file list and no bitmap, so
       the paste has to answer it as a picture insert with the same copy rule. The handler reads its
       IDataObject, so a DataObject built here drives the real code path without touching the user's
       clipboard (probes must never overwrite it). */
    static void ClipboardImageFilePasteCase(MainWindow window){
        string label="clipboard image file paste";
        try{
            SourceEditor editor=Source(window);
            ResetDocImageFolder();
            string docFolder=TempPictureFolder();
            string destination=Path.Combine(docFolder,"outside.png");
            string before=editor.Document.Text;
            int caret=editor.Document.GetLineByNumber(3).Offset;
            editor.Select(caret,0);Pump();
            /* The real key path: PasteClipboard -> PasteClipboardImage -> ClipboardImageFiles, with
               the clipboard's data object handed in directly (PasteClipboardImage(data)). */
            var data=new DataObject();
            data.SetData(DataFormats.FileDrop,new string[]{OutsidePath});
            bool answered=(bool)Call(window,"PasteClipboardImage",new object[]{data});
            Pump();
            string after=editor.Document.Text;
            string want=Markup(OutsideAlt,OutsideRef);
            Check(label+" a file list with an image is a picture paste",answered&&after==before.Insert(caret,want),"answered="+answered+", inserted='"+Excerpt(InsertedText(before,after))+"'");
            Check(label+" stages the picture in the shared folder first",File.Exists(destination)&&SameBytes(OutsidePath,destination),"destination='"+destination+"', exists="+File.Exists(destination));
            Call(window,"Undo");Pump();
            Check(label+" one undo restores the document",editor.Document.Text==before,Excerpt(editor.Document.Text));
            if(editor.Document.Text!=before)ResetEditor(window,BaseText);
            /* Two files in one paste: one link each, in file order, and one undo takes both out. */
            string aDestination=Path.Combine(docFolder,"a.png"),bDestination=Path.Combine(docFolder,"a-2.png");
            try{if(File.Exists(aDestination))File.Delete(aDestination);if(File.Exists(bDestination))File.Delete(bDestination);}catch(Exception){}
            var two=new DataObject();
            two.SetData(DataFormats.FileDrop,new string[]{ClashPathA,ClashPathB});
            string beforeTwo=editor.Document.Text;
            bool twoAnswered=(bool)Call(window,"PasteClipboardImage",new object[]{two});
            Pump();
            string afterTwo=editor.Document.Text;
            string wantTwo=Markup("a",StagedRef("a.png"))+"\n"+Markup("a-2",StagedRef("a-2.png"));
            Check(label+" two files paste two links in file order",twoAnswered&&afterTwo==beforeTwo.Insert(caret==beforeTwo.Length?beforeTwo.Length:Math.Min(caret,beforeTwo.Length),wantTwo)&&File.Exists(aDestination)&&File.Exists(bDestination),"inserted='"+Excerpt(InsertedText(beforeTwo,afterTwo))+"'");
            Call(window,"Undo");Pump();
            Check(label+" one undo takes both links out",editor.Document.Text==beforeTwo,Excerpt(editor.Document.Text));
            if(editor.Document.Text!=beforeTwo)ResetEditor(window,BaseText);
            /* A list with a document in it is not a picture paste: the text paste answers it. */
            var mixed=new DataObject();
            mixed.SetData(DataFormats.FileDrop,new string[]{OutsidePath,TxtPath});
            bool mixedAnswered=(bool)Call(window,"PasteClipboardImage",new object[]{mixed});
            Check(label+" a mixed file list is left to the text paste",!mixedAnswered&&editor.Document.Text==beforeTwo,"answered="+mixedAnswered+", changed="+(editor.Document.Text!=beforeTwo));
            /* Explorer copies the file names into the text format as well: the picture-file list
               must still win, or Ctrl+V would paste the names as text. */
            var withText=new DataObject();
            withText.SetData(DataFormats.FileDrop,new string[]{OutsidePath});
            withText.SetData(DataFormats.UnicodeText,OutsidePath+"\r\n");
            string beforeText=editor.Document.Text;
            bool textAnswered=(bool)Call(window,"PasteClipboardImage",new object[]{withText});
            Pump();
            string afterText=editor.Document.Text;
            Check(label+" a picture-file list wins over the file names in the text format",textAnswered&&afterText.IndexOf(Markup("outside",OutsideRef),StringComparison.Ordinal)>=0&&afterText.IndexOf(OutsidePath,StringComparison.Ordinal)<0,"answered="+textAnswered+", inserted='"+Excerpt(InsertedText(beforeText,afterText))+"'");
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=beforeText)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    // Untitled document: the picture goes to the shared temp folder (Documents\MDNote\images) with
    // an absolute link, which the first save then moves beside the file.
    static void UnsavedImageFileCopyCase(MainWindow window){
        string label="unsaved image file copy";
        try{
            SourceEditor editor=Source(window);
            string temp=TempPictureFolder();
            Directory.CreateDirectory(temp);
            string source=Path.Combine(OutsideDir,"untitled.png");
            File.Delete(source);
            File.Copy(OutsidePath,source);
            string destination=Path.Combine(temp,"untitled.png");
            File.Delete(destination);
            string markup=Markup("untitled",EscapeRef(destination.Replace('\\','/')));
            string before=editor.Document.Text;
            int caret=editor.Document.GetLineByNumber(1).Offset;
            editor.Select(caret,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{source}});
            Pump();
            string after=editor.Document.Text;
            Check(label+" copies into the shared picture folder with an absolute link",after==before.Insert(caret,markup),"inserted='"+Excerpt(InsertedText(before,after))+"'");
            Check(label+" made the copy",File.Exists(destination)&&SameBytes(source,destination),"destination='"+destination+"', exists="+File.Exists(destination));
            Call(window,"Undo");Pump();
            if(editor.Document.Text!=before)ResetEditor(window,BaseText);
        }catch(Exception ex){Check(label+" exception",false,ex.GetType().Name+": "+ex.Message);try{ResetEditor(window,BaseText);}catch(Exception){}}
    }
    static void RunWindowImage(){
        MainWindow window=null;
        BaseText=FixtureText;
        try{
            /* Every image drop in this run now copies into FixtureDir\doc, so the folder starts empty
               and each case reads the picture its own insert made (the -2 checks depend on it). */
            ResetDocImageFolder();
            window=OpenDocument(DocPath);
            WindowImageCase(window,"window image drop edit mode",SamePath,Markup(SameAlt,SameRefCopy),false);
            WindowImageCase(window,"window image drop read mode",SamePath,Markup(SameAlt,SameRefCopy),true);
            WindowMissingImageCase(window);
            ResetDocImageFolder();
            InsertImageFileCopyCase(window);
            ResetDocImageFolder();
            ClipboardImageFilePasteCase(window);
        }catch(Exception ex){Check("window image drop run",false,ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(window);}
    }
    static void RunUnsaved(){
        MainWindow window=null;
        BaseText=UnsavedText;
        try{
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();
            window.SetText(UnsavedText);
            Pump();
            window.SetMode(true);
            Pump();
            Check("unsaved window is a new Markdown document",Field<string>(window,"path")==""&&!Prop<bool>(window,"PlainText"),"path='"+Field<string>(window,"path")+"', plain="+Prop<bool>(window,"PlainText"));
            string markup=Markup(SameAlt,UnsavedRef);
            Check("unsaved reference is an absolute path (brief listed file:///)",markup.IndexOf("file:",StringComparison.OrdinalIgnoreCase)<0&&markup.IndexOf(UnsavedRef,StringComparison.Ordinal)>=0,"expected='"+markup+"' (MarkdownCommands.ImageReference uses an absolute path for an unsaved document; UserFixProbe.cs:1005-1012 pins that form)");
            /* The new copy rule: SamePath lies outside every folder of this untitled document, so
               the drop copies it into Documents\MDNote\images under its own name and the link names
               that copy, in the absolute form an untitled document writes. */
            string tempFolder=TempPictureFolder();
            Directory.CreateDirectory(tempFolder);
            string tempCopy=Path.Combine(tempFolder,Path.GetFileName(SamePath));
            try{if(File.Exists(tempCopy))File.Delete(tempCopy);}catch(Exception){}
            string escapedCopy=EscapeRef(tempCopy.Replace('\\','/'));
            markup=Markup(SameAlt,escapedCopy);
            RunImageCase(window,"unsaved image drop",SamePath,markup,1,3,0,false,null,-1);
            Check("unsaved image drop copies into the shared picture folder",File.Exists(tempCopy)&&SameBytes(SamePath,tempCopy),"copy='"+tempCopy+"', exists="+File.Exists(tempCopy));
            UnsavedImageFileCopyCase(window);
            SetRaw(window,true);
            RunImageCase(window,"unsaved raw image drop",SamePath,markup,2,3,0,false,null,-1);
            /* The probe's own copies in the shared pictures folder are removed again: the folder is
               the user's (Documents\MDNote\images), not this run's scratch space. */
            foreach(string copy in new string[]{tempCopy,Path.Combine(tempFolder,"untitled.png")}){
                try{if(File.Exists(copy))File.Delete(copy);}catch(Exception){}
            }
        }catch(Exception ex){Check("unsaved run",false,ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(window);BaseText=FixtureText;}
    }
    static void RunOpenAsDocument(){
        MainWindow window=null;
        try{
            window=OpenDocument(DocPath);
            int before=window.OpenDocumentCount;
            Point point=DropPoint(Source(window),3,2,0,false);
            DropOutcome svg=RaiseDrop(window,FileDrop(new string[]{SvgPath}),point,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,true);
            Pump();
            string activePath=Field<string>(window,"path");
            bool svgOpened=svg.Error==null&&!svg.PreviewHandled&&window.OpenDocumentCount==before+1&&String.Equals(activePath,Path.GetFullPath(SvgPath),StringComparison.OrdinalIgnoreCase)&&Text(window).IndexOf("<svg",StringComparison.Ordinal)>=0;
            Check("unsupported svg drop is not inserted and opens as a document",svgOpened,"previewHandled="+svg.PreviewHandled+", windowHandled="+svg.Handled+", documents="+window.OpenDocumentCount+", path='"+activePath+"'");
            int beforeTxt=window.OpenDocumentCount;
            Point point2=DropPoint(Source(window),1,2,0,false);
            DropOutcome text=RaiseDrop(window,FileDrop(new string[]{PickPath}),point2,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,true);
            Pump();
            string textPath=Field<string>(window,"path");
            bool textOpened=text.Error==null&&!text.PreviewHandled&&window.OpenDocumentCount==beforeTxt+1&&String.Equals(textPath,Path.GetFullPath(PickPath),StringComparison.OrdinalIgnoreCase)&&Text(window)==PickText;
            Check("text file drop opens as a document",textOpened,"previewHandled="+text.PreviewHandled+", windowHandled="+text.Handled+", documents="+window.OpenDocumentCount+", path='"+textPath+"', text='"+Excerpt(Text(window))+"'");
            string beforeMixed=Text(window);
            Point point3=DropPoint(Source(window),1,2,0,false);
            DropOutcome mixed=RaiseDrop(window,FileDrop(new string[]{SamePath,PickPath}),point3,DragDropEffects.Copy,DragDropKeyStates.LeftMouseButton,false);
            Pump();
            Check("mixed image/text drop is left to the open-as-document path",mixed.Error==null&&!mixed.Handled&&window.OpenDocumentCount==beforeTxt+1&&Text(window)==beforeMixed,"handled="+mixed.Handled+", documents="+window.OpenDocumentCount);
        }catch(Exception ex){Check("open-as-document run",false,ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(window);}
    }
    /* Staging and the save (user, 2026-11-03). An insert into a SAVED document leaves <docname>\ alone
       (the picture waits in the shared temp folder with an absolute link); every save moves the
       pictures the text still links into <docname>\ of the save target and rewrites the links; a
       Save As of a saved document copies the pictures it links from the old <docname>\ into the new
       one; a save that does not write puts everything back. All four run through the window's real
       Save (the SaveTargetPicker seam stands in for the native dialog). */
    static string ReadDisk(string file){return File.ReadAllText(file,new UTF8Encoding(false));}
    static int FileCount(string folder){return Directory.Exists(folder)?Directory.GetFiles(folder).Length:0;}
    static bool SaveWith(MainWindow window,bool saveAs,string pickTarget,out string error){
        error=null;
        Func<string,string> oldPicker=MainWindow.SaveTargetPicker;
        if(pickTarget!=null)MainWindow.SaveTargetPicker=delegate(string name){return pickTarget;};
        try{return (bool)Call(window,"Save",saveAs);}
        catch(TargetInvocationException ex){error=ex.InnerException!=null?ex.InnerException.GetType().Name+": "+ex.InnerException.Message:ex.Message;return false;}
        finally{MainWindow.SaveTargetPicker=oldPicker;Pump();}
    }
    static void RunStageSave(){
        MainWindow window=null;
        try{
            string root=Path.Combine(OutDir,"stage-save");
            if(Directory.Exists(root))Directory.Delete(root,true);
            Directory.CreateDirectory(root);
            ClearTempPictureFolder();
            string docFile=Path.Combine(root,"memo.md");
            File.WriteAllText(docFile,"# memo\n\nfirst line\nsecond line\n",new UTF8Encoding(false));
            string picA=Path.Combine(OutsideDir,"stage-a.png"),picB=Path.Combine(OutsideDir,"stage-b.jpg");
            File.Copy(OutsidePath,picA,true);
            WriteImage(picB,new System.Windows.Media.Imaging.JpegBitmapEncoder());
            string docFolder=Path.Combine(root,"memo");
            string stage=TempPictureFolder();
            string label="staged save";
            window=OpenDocument(docFile);
            SourceEditor editor=Source(window);
            string start=editor.Document.Text;
            editor.Select(editor.Document.GetLineByNumber(3).Offset,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{picA}});Pump();
            string afterA=editor.Document.Text;
            editor.Select(editor.Document.TextLength,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{picB}});Pump();
            string afterB=editor.Document.Text;
            string markA=Markup("stage-a",StagedRef("stage-a.png")),markB=Markup("stage-b",StagedRef("stage-b.jpg"));
            Check(label+" saved document links both pictures by their staged absolute path",afterB.IndexOf(markA,StringComparison.Ordinal)>=0&&afterB.IndexOf(markB,StringComparison.Ordinal)>=0,"text='"+Excerpt(afterB)+"'");
            Check(label+" insert staged the files and did not touch <docname>\\",File.Exists(Path.Combine(stage,"stage-a.png"))&&File.Exists(Path.Combine(stage,"stage-b.jpg"))&&!Directory.Exists(docFolder),"temp a="+File.Exists(Path.Combine(stage,"stage-a.png"))+", b="+File.Exists(Path.Combine(stage,"stage-b.jpg"))+", docFolder exists="+Directory.Exists(docFolder));
            /* A clipboard bitmap takes the same staging in a saved document. */
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(8,8,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            var visual=new System.Windows.Media.DrawingVisual();
            using(var context=visual.RenderOpen())context.DrawRectangle(System.Windows.Media.Brushes.Tomato,null,new Rect(0,0,8,8));
            bitmap.Render(visual);
            var bitmapData=new DataObject();bitmapData.SetData(DataFormats.Bitmap,bitmap);
            editor.Select(editor.Document.TextLength,0);Pump();
            string beforePaste=editor.Document.Text;
            int stagedBefore=FileCount(stage);
            bool pasted=(bool)Call(window,"PasteClipboardImage",new object[]{bitmapData});Pump();
            string afterPaste=editor.Document.Text;
            string pastedLink=EscapeRef(stage.Replace('\\','/')+"/image-");
            Check(label+" a clipboard bitmap is staged too and <docname>\\ stays absent",pasted&&afterPaste.IndexOf("!["+"image-",StringComparison.Ordinal)>=0&&afterPaste.IndexOf(pastedLink,StringComparison.Ordinal)>=0&&FileCount(stage)==stagedBefore+1&&!Directory.Exists(docFolder),"pasted="+pasted+", inserted='"+Excerpt(InsertedText(beforePaste,afterPaste))+"', staged "+stagedBefore+"->"+FileCount(stage));
            Call(window,"Undo");Pump();
            Check(label+" undo takes the pasted link out",editor.Document.Text==beforePaste,Excerpt(editor.Document.Text));
            /* Inserted, then removed before saving: the picture must never reach <docname>\. */
            Call(window,"Undo");Pump();
            Check(label+" undo takes the second picture's link out",editor.Document.Text==afterA,Excerpt(editor.Document.Text));
            Check(label+" the removed picture's file stays in the shared folder (nothing is deleted)",File.Exists(Path.Combine(stage,"stage-b.jpg")),"temp b="+File.Exists(Path.Combine(stage,"stage-b.jpg")));
            string error;
            bool saved=SaveWith(window,false,null,out error);
            string afterSave=editor.Document.Text;
            Check(label+" save succeeded",saved,error);
            string movedA=Path.Combine(docFolder,"stage-a.png");
            Check(label+" the linked picture moved into <docname>\\",File.Exists(movedA)&&SameBytes(picA,movedA)&&!File.Exists(Path.Combine(stage,"stage-a.png")),"moved="+File.Exists(movedA)+", still staged="+File.Exists(Path.Combine(stage,"stage-a.png")));
            Check(label+" the removed picture did not land in <docname>\\",!File.Exists(Path.Combine(docFolder,"stage-b.jpg"))&&FileCount(docFolder)==1,"files in <docname>\\ = "+FileCount(docFolder));
            Check(label+" the link is relative now",afterSave==afterA.Replace(markA,Markup("stage-a","memo/stage-a.png")),"text='"+Excerpt(afterSave)+"'");
            Check(label+" the file on disk holds the relative link",ReadDisk(docFile)==afterSave,"disk='"+Excerpt(ReadDisk(docFile))+"'");
            Call(window,"Undo");Pump();
            Check(label+" the link rewrite is one undo step",editor.Document.Text==afterA,Excerpt(editor.Document.Text));
            Call(window,"Redo");Pump();
            string again;
            bool savedAgain=SaveWith(window,false,null,out again);
            Check(label+" a second plain save changes nothing",savedAgain&&editor.Document.Text==afterSave&&FileCount(docFolder)==1&&ReadDisk(docFile)==afterSave,"saved="+savedAgain+", files="+FileCount(docFolder)+" "+again);

            /* Save As to another name and folder: the linked picture is COPIED, the rest is not. */
            File.Copy(OutsidePath,Path.Combine(docFolder,"unused.png"),true);
            string withExtras=editor.Document.Text+"\n![web](https://example.com/a.png)\n![beside](beside.png)\n";
            ResetEditor(window,withExtras);
            string newDir=Path.Combine(OutDir,"stage-save-new");
            if(Directory.Exists(newDir))Directory.Delete(newDir,true);
            Directory.CreateDirectory(newDir);
            string newDoc=Path.Combine(newDir,"새글.md");
            string newFolder=Path.Combine(newDir,"새글");
            string oldDisk=ReadDisk(docFile);
            bool savedAs=SaveWith(window,true,newDoc,out error);
            string afterAs=editor.Document.Text;
            label="save as copy";
            Check(label+" save succeeded and the tab now points at the new file",savedAs&&String.Equals(Field<string>(window,"path"),newDoc,StringComparison.OrdinalIgnoreCase),"saved="+savedAs+", path='"+Field<string>(window,"path")+"' "+error);
            string copiedA=Path.Combine(newFolder,"stage-a.png");
            Check(label+" copied the linked picture into the new <newname>\\",File.Exists(copiedA)&&SameBytes(picA,copiedA)&&FileCount(newFolder)==1,"copy="+File.Exists(copiedA)+", files="+FileCount(newFolder));
            Check(label+" left the old picture and folder as they were",File.Exists(movedA)&&File.Exists(Path.Combine(docFolder,"unused.png"))&&FileCount(docFolder)==2&&ReadDisk(docFile)==oldDisk,"old files="+FileCount(docFolder));
            Check(label+" rewrote only the copied picture's link",afterAs==withExtras.Replace(Markup("stage-a","memo/stage-a.png"),Markup("stage-a","새글/stage-a.png")),"text='"+Excerpt(afterAs)+"'");
            Check(label+" kept the web link and the unrelated relative link",afterAs.IndexOf("![web](https://example.com/a.png)",StringComparison.Ordinal)>=0&&afterAs.IndexOf("![beside](beside.png)",StringComparison.Ordinal)>=0,"text='"+Excerpt(afterAs)+"'");
            Check(label+" the new file on disk holds the new links",ReadDisk(newDoc)==afterAs,"disk='"+Excerpt(ReadDisk(newDoc))+"'");
            Call(window,"Undo");Pump();
            Check(label+" the link rewrite is one undo step",editor.Document.Text==withExtras,Excerpt(editor.Document.Text));
            SafeClose(window);window=null;

            /* A save that does not write (the target is open in another window) puts it all back. */
            string failRoot=Path.Combine(OutDir,"stage-fail");
            if(Directory.Exists(failRoot))Directory.Delete(failRoot,true);
            Directory.CreateDirectory(Path.Combine(failRoot,"src-doc"));
            string srcDoc=Path.Combine(failRoot,"src-doc.md"),dstDoc=Path.Combine(failRoot,"dst-doc.md");
            File.Copy(OutsidePath,Path.Combine(failRoot,"src-doc","old.png"),true);
            File.WriteAllText(srcDoc,"![old](src-doc/old.png)\n\nbody\n",new UTF8Encoding(false));
            File.WriteAllText(dstDoc,"other\n",new UTF8Encoding(false));
            string picC=Path.Combine(OutsideDir,"stage-c.png");
            File.Copy(OutsidePath,picC,true);
            MainWindow holder=OpenDocument(dstDoc);
            window=OpenDocument(srcDoc);
            editor=Source(window);
            editor.Select(editor.Document.TextLength,0);Pump();
            Call(window,"InsertImageFilesAtCaret",new object[]{new string[]{picC}});Pump();
            string beforeFail=editor.Document.Text;
            string notice=null;
            Action<string> oldNotice=MainWindow.SaveTargetInUseNotice;
            MainWindow.SaveTargetInUseNotice=delegate(string message){notice=message;};
            bool failed;string failError;
            try{failed=SaveWith(window,true,dstDoc,out failError);}finally{MainWindow.SaveTargetInUseNotice=oldNotice;}
            label="failed save";
            Check(label+" refused (the target is open elsewhere)",!failed&&notice!=null,"saved="+failed+", notice='"+Excerpt(notice)+"' "+failError);
            Check(label+" restored every link",editor.Document.Text==beforeFail,"text='"+Excerpt(editor.Document.Text)+"'");
            string failFolder=Path.Combine(failRoot,"dst-doc");
            Check(label+" removed the copies and moved the staged picture back",FileCount(failFolder)==0&&File.Exists(Path.Combine(stage,"stage-c.png"))&&File.Exists(Path.Combine(failRoot,"src-doc","old.png")),"new folder files="+FileCount(failFolder)+", staged c="+File.Exists(Path.Combine(stage,"stage-c.png")));
            SafeClose(holder);
        }catch(Exception ex){Check("stage save run",false,ex.GetType().Name+": "+ex.Message);}
        finally{SafeClose(window);}
    }
    static void WriteReport(){
        string marker=Path.Combine(OutDir,"drop-probe-"+(All?"PASS":"FAIL")+".txt");
        string opposite=Path.Combine(OutDir,"drop-probe-"+(All?"FAIL":"PASS")+".txt");
        try{if(File.Exists(opposite))File.Delete(opposite);}catch(Exception){}
        Report.Append('\n').Append("NOTES\n");
        Report.Append("- Unsaved documents use an absolute file path (MarkdownCommands.ImageReference) for a picture in the shared picture folder; the brief listed file:/// but the app's own test pins the absolute form (UserFixProbe.cs:1005-1012). The probe deletes the copies it makes in Documents\\MDNote\\images again.\n");
        Report.Append("- svg is not in MarkdownCommands.IsImageFile; an svg drop follows the open-as-document answer by design.\n");
        Report.Append("- The other-drive case does not create D:\\pics\\tail.png; it checks MarkdownCommands.ImageReference against the saved fixture document (D:/pics/tail.png) and that dropping the missing file inserts nothing.\n");
        Report.Append("- Drops are real routed PreviewDrop/Drop events with real DataObjects at TextView hit points; the OS drag pipeline (mouse capture, cursor) is not exercised.\n");
        Report.Append("- Plain-text and Ctrl+drag drops are left by the app to AvalonEdit's Drop handler; the inserted text is selected and CaretIndex ends after it. Caret checks read CaretIndex, not SelectionStart.\n");
        Report.Append("- The mixed image/text case raises PreviewDrop only: bubbling it would make the self-test open the binary png as a document (LoadFileCore rethrows in self-test), so the open-as-document result is covered by the svg/txt cases instead.\n");
        Report.Append("- Read mode runs in its own window (the state a .md opens in): an existing image drop turns editing on through the Ctrl+E entry point and inserts at the hit point, a drop in the empty space under the last line appends at the document end, and a name that is gone inserts nothing while the window still consumes the image-only drop (no document opens, no decoder box).\n");
        Report.Append("- Window-level drops (status bar, tab strip, ribbon, margins) are raised on the window itself: an all-image drop on a Markdown document inserts at the caret as one undo step, read mode included, and an all-image drop never reaches LoadFile. Mixed drops keep the open-as-document answer.\n");
        Report.Append("- An image dropped inside a fenced code block is refused: a token written there would be literal text, so the source consumes the drop and neither the text nor the caret changes.\n");
        Report.Append("- Staging rule (2026-11-03): only a picture inside the document's own picture folder (<docdir>\\<docname>, subfolders included) is linked where it is. Any other inserted picture, saved document or not, is copied into the shared picture folder (the probe points it at <dir>\\temp-images through MainWindow.ImageTempFolderOverride; original name, an identical file is reused, a different one with that name takes -2) and linked by its absolute path. Every save moves the pictures the text still links from that folder into <docname>\\ of the target and rewrites the links to relative; a Save As of a saved document copies the pictures it links from the old <docname>\\; a save that does not write restores both (RunStageSave). A failed copy keeps the original path. The same rule covers a drop, a Ctrl+V of copied files, a clipboard bitmap and the image button's dialog.\n");
        Report.Append("- Elapsed ").Append(Clock.ElapsedMilliseconds).Append(" ms.\n");
        File.WriteAllText(Path.Combine(OutDir,"drop-probe.txt"),Report.ToString(),new UTF8Encoding(false));
        File.WriteAllText(marker,"DropProbe "+(All?"PASS":"FAIL")+"  "+Passes+" PASS, "+Fails+" FAIL, "+Clock.ElapsedMilliseconds+" ms\n",new UTF8Encoding(false));
    }
    internal static int Run(string dir){
        OutDir=Path.GetFullPath(dir);
        Directory.CreateDirectory(OutDir);
        Clock=Stopwatch.StartNew();
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Dispatcher.CurrentDispatcher.UnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs args){DispatchError=args.Exception;args.Handled=true;};
        Report.Append("DropProbe (release test B9)  ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
        try{
            PrepareFixture();
            RunMode(false);
            RunMode(true);
            RunReadMode();
            RunTxtMode();
            RunWindowImage();
            RunUnsaved();
            RunStageSave();
            RunOpenAsDocument();
        }catch(Exception ex){Check("probe run",false,ex.GetType().Name+": "+ex.Message);}
        MainWindow.ImageTempFolderOverride=null;
        Clock.Stop();
        Report.Append('\n').Append("SUMMARY: ").Append(Passes).Append(" PASS, ").Append(Fails).Append(" FAIL, ").Append(Clock.ElapsedMilliseconds).Append(" ms\n");
        WriteReport();
        return All?0:1;
    }
}
}
