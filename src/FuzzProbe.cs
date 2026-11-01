using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
namespace MDNote {public partial class MainWindow {
internal static int VerifyFuzz(string outDir){return FuzzProbeRunner.Run(outDir);}
// Fuzz probe (self-test only, generated data). Seeds 1..40 build markdown documents with headings,
// styled paragraphs, nested bullet/ordered/task lists, quotes with lists, tables, fenced code,
// HTML entities, backslash escapes, Korean and emoji; even seeds use CRLF. Each seed runs:
//   A. no-edit round trip: open a tab and move basic->live->basic and read<->edit; after every step
//      the buffer text, the dirty flag and the saved bytes must equal the generated file.
//   B. live edit oracle: 30 random source edits (insert/delete/replace, 1-20 chars) are applied to
//      the AvalonEdit document and to a plain string in parallel. The saved text must equal the
//      oracle normalized to the document newline; every edit is then undone with the app's Undo
//      command (original text required) and redone (the raw edit oracle required).
//      Saving normalizes only the bytes on disk: the buffer and the history keep the raw edits.
//   C. basic-view stability: switching to the rich view and back after B must keep text and dirty.
// The helper class cannot be named "FuzzProbe": MainWindow already has an instance method with that
// name (RC5Tests.cs, the old rich-view fuzz), so nesting a type of the same name is CS0102.
internal static class FuzzProbeRunner {
    const int FirstSeed=1,LastSeed=40,EditCount=30;
    static readonly char[] EditPool=new char[]{'가','나','다','라','마','한','글','테','스','트','배','열','x','Z','7',' ','\n','\t','|','*','-','_','`','[',']','(',')','#','>','&'};
    static readonly string[] Words=new string[]{"항목","값","이름","상태","alpha","beta","gamma","완료","note","row"};
    static StringBuilder report;static bool all;static int cases,passed,failed;static string outRoot;
    internal static int Run(string outDir){
        outRoot=Path.GetFullPath(outDir);Directory.CreateDirectory(outRoot);
        var seedList=new StringBuilder();for(int seed=FirstSeed;seed<=LastSeed;seed++){if(seed>FirstSeed)seedList.Append(',');seedList.Append(seed);}
        report=new StringBuilder("# fuzz-probe\n");
        report.Append("# SEEDS: ").Append(seedList.ToString()).Append(" (even seeds CRLF, odd seeds LF)\n");
        report.Append("# Per seed: no-edit round trip; 30 live source edits vs a string oracle plus undo/redo; basic-view stability.\n");
        all=true;cases=passed=failed=0;
        string passFile=Path.Combine(outRoot,"fuzz-probe-PASS.txt"),failFile=Path.Combine(outRoot,"fuzz-probe-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        bool oldLive=ProbeDefaults.Live,oldSelf=Program.IsSelfTest;
        var watch=new System.Diagnostics.Stopwatch();watch.Start();
        try{
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            for(int seed=FirstSeed;seed<=LastSeed;seed++)RunSeed(seed);
        }catch(Exception ex){all=false;Line("EXCEPTION: "+OneLine(ex.ToString()));}
        finally{ProbeDefaults.Live=oldLive;Program.IsSelfTest=oldSelf;}
        watch.Stop();
        report.Append("\nCases: ").Append(cases).Append("  PASS: ").Append(passed).Append("  FAIL: ").Append(failed).Append('\n');
        report.Append("Result: ").Append(all?"PASS":"FAIL").Append('\n');
        report.Append("Runtime: ").Append(watch.ElapsedMilliseconds).Append(" ms\n");
        try{
            File.WriteAllText(Path.Combine(outRoot,"fuzz-probe.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(all?passFile:failFile,report.ToString(),new UTF8Encoding(false));
        }catch(Exception){return 2;}
        return all?0:1;
    }
    static void RunSeed(int seed){
        string tag="s"+seed.ToString("00",CultureInfo.InvariantCulture);
        string newline=seed%2==0?"\r\n":"\n";
        string content=FuzzDocument(seed,newline);
        string file=Path.Combine(outRoot,"fuzz-seed-"+seed.ToString("00",CultureInfo.InvariantCulture)+".md");
        File.WriteAllText(file,content,new UTF8Encoding(false));
        byte[] original=File.ReadAllBytes(file);
        MainWindow window=null;
        try{
            window=CreateWindow();
            window.LoadFile(file);LongLineBench.Idle();window.UpdateLayout();
            // One engine: Markdown bodies always open on the source surface. The open and every
            // Basic/Live view-mode and read/edit step must leave the buffer, dirty flag and saved
            // bytes equal to the generated file.
            Check(tag+".open.surface",window.CodeMode&&!window.editing,"code="+window.CodeMode+", editing="+window.editing);
            NoEditStep(tag+".open",window,content,original);
            window.SetLiveMode(true);LongLineBench.Idle();window.UpdateLayout();
            Check(tag+".live.entered",window.CodeMode&&window.LivePreviewAttached,"code="+window.CodeMode+", attached="+window.LivePreviewAttached);
            NoEditStep(tag+".live.read",window,content,original);
            window.SetMode(true);LongLineBench.Idle();
            Check(tag+".live.edit.on",window.editing&&window.CodeMode,"editing="+window.editing+", code="+window.CodeMode);
            NoEditStep(tag+".live.edit",window,content,original);
            window.SetMode(false);LongLineBench.Idle();
            Check(tag+".live.edit.off",!window.editing&&window.CodeMode,"editing="+window.editing+", code="+window.CodeMode);
            NoEditStep(tag+".live.read.back",window,content,original);
            window.SetLiveMode(false);LongLineBench.Idle();window.UpdateLayout();
            Check(tag+".basic.read.surface",window.CodeMode&&!window.editing&&hideMarkers,"code="+window.CodeMode+", hideMarkers="+hideMarkers+", editing="+window.editing);
            NoEditStep(tag+".basic.read",window,content,original);
            window.SetMode(true);LongLineBench.Idle();
            Check(tag+".basic.edit.on",window.editing&&window.CodeMode,"editing="+window.editing+", code="+window.CodeMode);
            NoEditStep(tag+".basic.edit",window,content,original);
            window.SetMode(false);LongLineBench.Idle();
            NoEditStep(tag+".basic.read.back",window,content,original);
            window.SetLiveMode(true);LongLineBench.Idle();window.UpdateLayout();
            NoEditStep(tag+".live.again.read",window,content,original);
            window.SetMode(true);LongLineBench.Idle();
            Check(tag+".live.again.edit",window.editing&&window.CodeMode,"editing="+window.editing+", code="+window.CodeMode);
            NoEditStep(tag+".live.again.edit.check",window,content,original);
            RunEditOracle(tag,seed,window,content);
            RunBasicStability(tag,window);
        }catch(Exception ex){cases++;failed++;all=false;Line("FAIL "+tag+".exception: "+OneLine(ex.GetType().Name+": "+ex.Message));}
        finally{CloseWindow(window);}
    }
    static void NoEditStep(string name,MainWindow window,string content,byte[] original){
        window.FlushDocumentChanges();
        bool dirty=window.HasUnsavedChanges;
        string buffer=window.text;
        bool saved=false;string error=null;
        try{saved=window.Save(false);}catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;}
        byte[] now=null;string savedText=null;bool bytesEqual=false;
        try{now=File.ReadAllBytes(window.path);savedText=new UTF8Encoding(false).GetString(now);bytesEqual=SameBytes(now,original);}
        catch(Exception ex){error=(error==null?"":error+" | ")+ex.GetType().Name+": "+ex.Message;}
        string detail="dirty="+dirty+", save="+saved+", bufferEqual="+(buffer==content)+", savedTextEqual="+(savedText==content)+", bytesEqual="+bytesEqual;
        if(buffer!=content)detail+=", bufferDiff: "+Diff(content,buffer);
        if(!bytesEqual)detail+=", firstByte="+FirstByte(original,now)+", savedDiff: "+Diff(content,savedText);
        if(error!=null)detail+=", error="+error;
        Check(name,!dirty&&saved&&buffer==content&&savedText==content&&bytesEqual,detail);
    }
    // ------------------------------------------------ B live edit oracle
    static void RunEditOracle(string tag,int seed,MainWindow window,string content){
        var random=new Random(seed*7919+101);
        var oracle=new StringBuilder(content);
        Exception editError=null;
        try{
            for(int step=0;step<EditCount;step++)NextEdit(random,window,oracle);
            LongLineBench.Idle();
        }catch(Exception ex){editError=ex;}
        window.FlushDocumentChanges();
        string buffer=window.text;
        Check(tag+".edits.buffer",editError==null&&buffer==oracle.ToString(),
            editError!=null?"edit threw: "+OneLine(editError.GetType().Name+": "+editError.Message):buffer==oracle.ToString()?"bufferEqual=true":Diff(oracle.ToString(),buffer));
        bool saved=false;string saveError=null;
        try{saved=window.Save(false);}catch(Exception ex){saveError=ex.GetType().Name+": "+ex.Message;}
        string expected=NormalizeLineEndings(oracle.ToString(),window.documentNewline);
        string savedText=null;
        try{savedText=new UTF8Encoding(false).GetString(File.ReadAllBytes(window.path));}catch(Exception ex){saveError=(saveError==null?"":saveError+" | ")+ex.GetType().Name+": "+ex.Message;}
        Check(tag+".edits.saved",saveError==null&&saved&&savedText==expected,
            saveError!=null?"save error: "+OneLine(saveError):"save="+saved+", savedEqual="+(savedText==expected)+", normalized="+(expected!=oracle.ToString())+(savedText==expected?"":", "+Diff(expected,savedText)));
        // SaveToPath normalizes only the bytes on disk: the buffer keeps the raw edits, so undo-all
        // walks exactly the recorded edits (no normalization entry) and redo-all restores the raw
        // buffer, whose newlines can differ from the saved text.
        string raw=oracle.ToString();
        bool bufferKept=window.text==raw;
        Exception undoError=null;int undoSteps=0;
        try{while(window.History.CanUndo&&undoSteps<EditCount*4){window.Undo();undoSteps++;}}
        catch(Exception ex){undoError=ex;}
        window.FlushDocumentChanges();
        string undone=window.text;
        Check(tag+".undo.all",undoError==null&&bufferKept&&undoSteps>0&&undone==content,
            undoError!=null?"undo threw after "+undoSteps+" steps: "+OneLine(undoError.GetType().Name+": "+undoError.Message)+", savedRaw="+(window.saved==raw)+", sourceLength="+window.source.Document.TextLength+", savedLength="+window.saved.Length:"undoSteps="+undoSteps+", bufferKept="+bufferKept+", dirty="+window.HasUnsavedChanges+(undone==content?"":", "+Diff(content,undone)));
        Exception redoError=null;int redoSteps=0;
        try{while(window.History.CanRedo&&redoSteps<EditCount*4){window.Redo();redoSteps++;}}
        catch(Exception ex){redoError=ex;}
        window.FlushDocumentChanges();
        string redone=window.text;
        Check(tag+".redo.all",redoError==null&&redoSteps==undoSteps&&redone==raw&&!window.HasUnsavedChanges,
            redoError!=null?"redo threw after "+redoSteps+" steps: "+OneLine(redoError.GetType().Name+": "+redoError.Message):"redoSteps="+redoSteps+" of "+undoSteps+", dirty="+window.HasUnsavedChanges+(redone==raw?"":", "+Diff(raw,redone)));
    }
    // One random edit: insert, delete or replace 1-20 chars. Every edit is applied to the document
    // with the same arguments as to the string builder, then the dispatcher is drained so queued
    // live-preview work cannot leak into the next step.
    static void NextEdit(Random random,MainWindow window,StringBuilder oracle){
        int length=oracle.Length;
        int op=length==0?0:random.Next(3);
        int offset=length==0?0:random.Next(length+1);
        int remove=0;string insert=null;
        if(op!=0&&length>0){
            if(offset==length)offset=length-1;
            remove=1+random.Next(Math.Min(20,length-offset));
        }
        int insertionOffset,safeLength;SafeRange(oracle,offset,remove,out insertionOffset,out safeLength);
        offset=insertionOffset;remove=safeLength;
        if(op!=0&&remove==0)op=0;
        if(op!=1)insert=EditText(random);
        if(op==0){window.source.Document.Insert(offset,insert);oracle.Insert(offset,insert);}
        else if(op==1){window.source.Document.Remove(offset,remove);oracle.Remove(offset,remove);}
        else{window.source.Document.Replace(offset,remove,insert);oracle.Remove(offset,remove);oracle.Insert(offset,insert);}
        LongLineBench.Idle();
    }
    // Deletions must not split a surrogate pair: half of an emoji is not a character a user can
    // produce, and the strict UTF-8 encoder refuses to save it. Move a range start off a low
    // surrogate, shrink an end that splits a pair, and keep the range at 20 code units or less.
    static void SafeRange(StringBuilder text,int offset,int length,out int safeOffset,out int safeLength){
        int start=offset;
        if(start>0&&start<text.Length&&Char.IsLowSurrogate(text[start])&&Char.IsHighSurrogate(text[start-1]))start--;
        int end=Math.Min(text.Length,start+length);
        if(end>start&&end<text.Length&&Char.IsLowSurrogate(text[end])&&Char.IsHighSurrogate(text[end-1]))end--;
        if(end-start>20){
            end=start+20;
            if(end<text.Length&&Char.IsLowSurrogate(text[end])&&Char.IsHighSurrogate(text[end-1]))end--;
        }
        safeOffset=start;safeLength=end-start;
    }
    static string EditText(Random random){
        int length=1+random.Next(20);var value=new StringBuilder(length+2);
        while(value.Length<length){
            if(value.Length+2<=length&&random.Next(6)==0)value.Append("😀");
            else value.Append(EditPool[random.Next(EditPool.Length)]);
        }
        return value.ToString();
    }
    // ------------------------------------------------ C view-mode stability
    static void RunBasicStability(string tag,MainWindow window){
        string before=window.text;bool dirty=window.HasUnsavedChanges;Exception error=null;
        string sourceBefore=window.source.Document.Text;
        try{window.SetLiveMode(false);LongLineBench.Idle();window.UpdateLayout();}catch(Exception ex){error=ex;}
        string basic=window.text;bool basicDirty=window.HasUnsavedChanges;
        Check(tag+".basic.stable",error==null&&basic==before&&basicDirty==dirty,
            (error!=null?"switch threw: "+OneLine(error.GetType().Name+": "+error.Message)+", ":"")+"textEqual="+(basic==before)+", dirtyBefore="+dirty+", dirtyAfter="+basicDirty+", sourceSnapshotDirty="+window.sourceSnapshotDirty+", sourceEqual="+(window.source.Document.Text==sourceBefore)+(basic==before?"":", "+Diff(before,basic)));
        error=null;
        try{window.SetLiveMode(true);LongLineBench.Idle();window.UpdateLayout();}catch(Exception ex){error=ex;}
        string live=window.text;bool liveDirty=window.HasUnsavedChanges;
        Check(tag+".live.stable",error==null&&live==before&&liveDirty==dirty,
            (error!=null?"switch threw: "+OneLine(error.GetType().Name+": "+error.Message)+", ":"")+"textEqual="+(live==before)+", dirtyBefore="+dirty+", dirtyAfter="+liveDirty+(live==before?"":", "+Diff(before,live)));
    }
    // ------------------------------------------------ helpers
    static MainWindow CreateWindow(){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        window.Show();window.SetMode(true);LongLineBench.Idle();window.UpdateLayout();
        return window;
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{
            window.FlushDocumentChanges();window.saved=window.text;window.CaptureTab();
            foreach(var document in window.documents)document.Saved=document.Text;
            window.Close();
        }catch(Exception){}
    }
    static void Check(string name,bool ok,string detail){
        cases++;if(ok)passed++;else failed++;all&=ok;
        report.Append(ok?"PASS ":"FAIL ").Append(name);
        if(!String.IsNullOrEmpty(detail))report.Append(": ").Append(OneLine(detail));
        report.Append('\n');
    }
    static void Line(string value){report.Append(value).Append('\n');}
    static string OneLine(string value){return value==null?"<null>":value.Replace("\r\n","\\n").Replace("\r","\\r").Replace("\n","\\n");}
    static string Diff(string want,string got){
        if(want==null)want="<null>";if(got==null)got="<null>";
        int at=0,limit=Math.Min(want.Length,got.Length);
        while(at<limit&&want[at]==got[at])at++;
        return "firstDiff="+at+", want="+Context(want,at)+", got="+Context(got,at);
    }
    static string Context(string value,int at){
        int start=Math.Max(0,at-20),end=Math.Min(value.Length,at+20);
        return "\""+Escape(value.Substring(start,end-start))+"\"";
    }
    static string Escape(string value){return value.Replace("\\","\\\\").Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t");}
    static int FirstByte(byte[] want,byte[] got){
        if(want==null||got==null)return -1;
        int limit=Math.Min(want.Length,got.Length),at=0;
        while(at<limit&&want[at]==got[at])at++;
        return at;
    }
    static bool SameBytes(byte[] left,byte[] right){
        if(left==null||right==null||left.Length!=right.Length)return false;
        for(int i=0;i<left.Length;i++)if(left[i]!=right[i])return false;
        return true;
    }
    // ------------------------------------------------ generated documents
    internal static string FuzzDocument(int seed,string newline){
        var random=new Random(seed);
        int target=30+random.Next(171);
        var lines=new List<string>();
        while(lines.Count<target){
            string[] block=Block(random);
            if(lines.Count+block.Length>200){if(lines.Count>=200)break;lines.Add(Paragraph(random));continue;}
            lines.AddRange(block);
        }
        var value=new StringBuilder();
        foreach(string line in lines)value.Append(line).Append(newline);
        return value.ToString();
    }
    static string[] Block(Random random){
        switch(random.Next(10)){
            case 0:return Heading(random);
            case 1:return ParagraphBlock(random);
            case 2:return BulletList(random);
            case 3:return OrderedList(random);
            case 4:return QuoteBlock(random);
            case 5:return TableBlock(random);
            case 6:return CodeBlock(random);
            case 7:return EntitiesLine(random);
            case 8:return EscapeLine(random);
            default:return BlankRun(random);
        }
    }
    static string[] Heading(Random random){int level=1+random.Next(4);return new string[]{new string('#',level)+" "+Phrase(random)};}
    static string[] ParagraphBlock(Random random){int count=1+random.Next(3);var lines=new string[count];for(int i=0;i<count;i++)lines[i]=Paragraph(random);return lines;}
    static string Paragraph(Random random){
        string[] shapes={
            "일반 문장입니다. **굵게** *기울임* ~~취소선~~ `인라인 코드` 끝.",
            "한글 문장과 English words 그리고 **강조**가 섞여 있습니다.",
            "A [링크](https://example.invalid/page) and `a|b*c-d` inline code.",
            "색상 <span style=\"color:#C03030\">빨간 글자</span> 와 크기 <span style=\"font-size:22px\">큰 글자</span>.",
            "Emoji 😀 🚀 👨‍👩‍👧‍👦 🇰🇷 and Korean 한글.",
            "[문서 링크](https://example.invalid/a/b?c=d&e=f) 끝.",
            "**한글 굵게**와 *한글 기울임* 그리고 ~~한글 취소선~~.",
            "접두 <span style=\"font-size:12px\">작은 글자</span> 접미."
        };
        return shapes[random.Next(shapes.Length)];
    }
    static string[] BulletList(Random random){
        var lines=new List<string>();
        int items=2+random.Next(3);
        for(int i=0;i<items;i++){
            lines.Add("- "+ListText(random));
            if(random.Next(3)==0){
                lines.Add("  1. "+ListText(random));
                if(random.Next(2)==0)lines.Add("     - "+ListText(random));
            }else if(random.Next(3)==0)lines.Add("  - "+ListText(random));
            if(random.Next(3)==0)lines.Add("- ["+(random.Next(2)==0?" ":"x")+"] "+TaskText(random));
        }
        return lines.ToArray();
    }
    static string[] OrderedList(Random random){
        int count=2+random.Next(3);var lines=new List<string>();
        for(int i=1;i<=count;i++){
            lines.Add(i+". "+ListText(random));
            if(i==1&&random.Next(2)==0){lines.Add("   - "+ListText(random));if(random.Next(2)==0)lines.Add("     - "+ListText(random));}
        }
        return lines.ToArray();
    }
    static string[] QuoteBlock(Random random){
        var lines=new List<string>();
        lines.Add("> "+QuoteText(random));lines.Add("> "+QuoteText(random));
        if(random.Next(2)==0){lines.Add("> - "+ListText(random));lines.Add(">   - "+ListText(random));}
        if(random.Next(3)==0)lines.Add("> 1. "+ListText(random));
        if(random.Next(4)==0)lines.Add(">");
        return lines.ToArray();
    }
    static string[] TableBlock(Random random){
        int columns=2+random.Next(4);var lines=new List<string>();
        var header=new List<string>();var align=new List<string>();
        for(int column=0;column<columns;column++){header.Add(Cell(random,true));align.Add(Alignment(random));}
        lines.Add("| "+String.Join(" | ",header.ToArray())+" |");
        lines.Add("| "+String.Join(" | ",align.ToArray())+" |");
        int rows=1+random.Next(3);
        for(int row=0;row<rows;row++){
            var cells=new List<string>();
            for(int column=0;column<columns;column++)cells.Add(Cell(random,false));
            lines.Add("| "+String.Join(" | ",cells.ToArray())+" |");
        }
        return lines.ToArray();
    }
    static string Alignment(Random random){switch(random.Next(4)){case 0:return ":---";case 1:return ":---:";case 2:return "---:";default:return "---";}}
    static string Cell(Random random,bool header){
        if(!header&&random.Next(4)==0)return "";
        string word=Words[random.Next(Words.Length)];
        if(!header&&random.Next(5)==0)return word+"\\|"+Words[random.Next(Words.Length)];
        if(!header&&random.Next(6)==0)return "`"+word+"`";
        return word;
    }
    static string[] CodeBlock(Random random){
        string[] infos={"csharp","text","","json"};var lines=new List<string>();
        lines.Add("```"+infos[random.Next(infos.Length)]);
        string[] body={
            "static int Sum(int a, int b) { return a + b; }",
            "var rows = items.Where(x => x.Ok).ToList();",
            "| pipe | and * star - dash",
            "한글 주석 // 값 = 42",
            "{\"key\": \"값\", \"n\": 1}",
            "if (a < b) { print(\"&amp;\"); }"
        };
        int count=2+random.Next(3);
        for(int i=0;i<count;i++)lines.Add(body[random.Next(body.Length)]);
        lines.Add("```");
        return lines.ToArray();
    }
    static string[] EntitiesLine(Random random){
        string[] shapes={
            "HTML entities: &amp; &lt; &gt; &quot; &nbsp; &copy; &#42; &mdash;",
            "엔티티 &amp; 와 &#x1F600; 그리고 &lt;tag&gt;.",
            "Mixed &quot;quotes&quot; and &copy; 2026."
        };
        return new string[]{shapes[random.Next(shapes.Length)]};
    }
    static string[] EscapeLine(Random random){
        string[] shapes={
            "Backslash escapes: \\*not italic\\* \\[not link\\] \\# not heading \\\\ backslash",
            "이스케이프: \\`code 아님\\` \\| pipe \\- dash \\_underscore\\_",
            "Escapes \\> quote \\* star \\~ tilde \\\\ end"
        };
        return new string[]{shapes[random.Next(shapes.Length)]};
    }
    static string[] BlankRun(Random random){int count=1+random.Next(3);var lines=new string[count];for(int i=0;i<count;i++)lines[i]="";return lines;}
    static string Phrase(Random random){string[] pool={"제목 문장","Heading with **bold**","한글 제목","Release notes","작업 목록","Second level"};return pool[random.Next(pool.Length)];}
    static string ListText(Random random){string[] pool={"항목 하나","두 번째 항목","**굵은 항목**","`코드` 항목","item with *star*","값 | 파이프","완료된 일"};return pool[random.Next(pool.Length)];}
    static string TaskText(Random random){string[] pool={"미완료 작업","완료한 작업","한글 작업 **강조**","task item"};return pool[random.Next(pool.Length)];}
    static string QuoteText(Random random){string[] pool={"인용문 첫 줄","두 번째 인용 줄 **강조**","quote with `code`","한글 인용 — 대시"};return pool[random.Next(pool.Length)];}
}}}
