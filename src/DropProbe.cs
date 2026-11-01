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
    static void PrepareFixture(){
        OutDir=Path.GetFullPath(OutDir);
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
        OtherPath="D:\\pics\\tail.png";
        UnsavedRef=EscapeRef(SameFull.Replace('\\','/'));
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
            string markup=Markup(SameAlt,SameRef);
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
            string markup=Markup(SameAlt,SameRef);
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
            string want=Markup(SameAlt,SameRef)+"\n"+Markup(SubAlt,SubRef);
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
            RunImageCase(window,mode+" image paragraph",SamePath,Markup(SameAlt,SameRef),3,3,0,false,null,-1);
            RunImageCase(window,mode+" image list item",SamePath,Markup(SameAlt,SameRef),6,5,0,false,"- ",-1);
            RunImageCase(window,mode+" image heading",SamePath,Markup(SameAlt,SameRef),1,4,0,false,"# ",-1);
            TableImageCase(window,mode);
            CodeImageCase(window,mode);
            RunImageCase(window,mode+" image document end",SamePath,Markup(SameAlt,SameRef),19,lastLength+1,120,true,null,-1);
            MultipleImagesCase(window,mode);
            if(!raw){
                RunImageCase(window,mode+" image same folder",SamePath,Markup(SameAlt,SameRef),3,3,0,false,null,-1);
                RunImageCase(window,mode+" image sub folder",SubPath,Markup(SubAlt,SubRef),3,3,0,false,null,-1);
                // The other-drive form is a path rule, not an insert: D:\pics\tail.png is not on disk
                // and the probe must not create it, so check what the shared reference helper writes.
                string otherReference=MarkdownCommands.ImageReference(DocPath,OtherPath);
                Check(mode+" image other drive path stays absolute",otherReference=="D:/pics/tail.png","reference='"+otherReference+"', document='"+DocPath+"'");
                RunImageCase(window,mode+" image jpg",JpgPath,Markup(JpgAlt,JpgRef),3,3,0,false,null,-1);
                RunImageCase(window,mode+" image gif",GifPath,Markup(GifAlt,GifRef),4,3,0,false,null,-1);
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
            ReadModeImageCase(window,"read mode image",SamePath,Markup(SameAlt,SameRef),3,3);
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
    static void RunWindowImage(){
        MainWindow window=null;
        BaseText=FixtureText;
        try{
            window=OpenDocument(DocPath);
            WindowImageCase(window,"window image drop edit mode",SamePath,Markup(SameAlt,SameRef),false);
            WindowImageCase(window,"window image drop read mode",SamePath,Markup(SameAlt,SameRef),true);
            WindowMissingImageCase(window);
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
            RunImageCase(window,"unsaved image drop",SamePath,markup,1,3,0,false,null,-1);
            SetRaw(window,true);
            RunImageCase(window,"unsaved raw image drop",SamePath,markup,2,3,0,false,null,-1);
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
    static void WriteReport(){
        string marker=Path.Combine(OutDir,"drop-probe-"+(All?"PASS":"FAIL")+".txt");
        string opposite=Path.Combine(OutDir,"drop-probe-"+(All?"FAIL":"PASS")+".txt");
        try{if(File.Exists(opposite))File.Delete(opposite);}catch(Exception){}
        Report.Append('\n').Append("NOTES\n");
        Report.Append("- Unsaved documents use an absolute file path (MarkdownCommands.ImageReference); the brief listed file:/// but the app's own test pins the absolute form (UserFixProbe.cs:1005-1012).\n");
        Report.Append("- svg is not in MarkdownCommands.IsImageFile; an svg drop follows the open-as-document answer by design.\n");
        Report.Append("- The other-drive case does not create D:\\pics\\tail.png; it checks MarkdownCommands.ImageReference against the saved fixture document (D:/pics/tail.png) and that dropping the missing file inserts nothing.\n");
        Report.Append("- Drops are real routed PreviewDrop/Drop events with real DataObjects at TextView hit points; the OS drag pipeline (mouse capture, cursor) is not exercised.\n");
        Report.Append("- Plain-text and Ctrl+drag drops are left by the app to AvalonEdit's Drop handler; the inserted text is selected and CaretIndex ends after it. Caret checks read CaretIndex, not SelectionStart.\n");
        Report.Append("- The mixed image/text case raises PreviewDrop only: bubbling it would make the self-test open the binary png as a document (LoadFileCore rethrows in self-test), so the open-as-document result is covered by the svg/txt cases instead.\n");
        Report.Append("- Read mode runs in its own window (the state a .md opens in): an existing image drop turns editing on through the Ctrl+E entry point and inserts at the hit point, a drop in the empty space under the last line appends at the document end, and a name that is gone inserts nothing while the window still consumes the image-only drop (no document opens, no decoder box).\n");
        Report.Append("- Window-level drops (status bar, tab strip, ribbon, margins) are raised on the window itself: an all-image drop on a Markdown document inserts at the caret as one undo step, read mode included, and an all-image drop never reaches LoadFile. Mixed drops keep the open-as-document answer.\n");
        Report.Append("- An image dropped inside a fenced code block is refused: a token written there would be literal text, so the source consumes the drop and neither the text nor the caret changes.\n");
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
            RunOpenAsDocument();
        }catch(Exception ex){Check("probe run",false,ex.GetType().Name+": "+ex.Message);}
        Clock.Stop();
        Report.Append('\n').Append("SUMMARY: ").Append(Passes).Append(" PASS, ").Append(Fails).Append(" FAIL, ").Append(Clock.ElapsedMilliseconds).Append(" ms\n");
        WriteReport();
        return All?0:1;
    }
}
}
