using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
namespace MDNote {
internal sealed class SearchHighlight : Adorner {
    internal int SourceStart=-1,SourceLength;
    internal int RectCount;
    List<Rect> geometry=new List<Rect>();
    readonly List<int> realSpans=new List<int>();
    /* E2: while the user is still typing a query no match is the "current" one, but every match
       the reader can see must be marked (the old engine did this and the live source path only
       ever drew the one current match). Extras are the other matches as source ranges. */
    readonly List<int> extraStarts=new List<int>(),extraLengths=new List<int>();
    internal int ExtraCount{get{return extraStarts.Count;}}
    internal SearchHighlight(UIElement element):base(element){IsHitTestVisible=false;}
    internal void Clear(){SourceStart=-1;SourceLength=0;extraStarts.Clear();extraLengths.Clear();Reposition();}
    internal void SetExtras(List<int> starts,List<int> lengths){
        extraStarts.Clear();extraLengths.Clear();
        if(starts==null||lengths==null)return;
        int count=Math.Min(starts.Count,lengths.Count);
        for(int i=0;i<count;i++)if(lengths[i]>0){extraStarts.Add(starts[i]);extraLengths.Add(lengths[i]);}
    }
    internal void Reposition(){
        var rectangles=new List<Rect>();var source=AdornedElement as SourceEditor;
        if(source!=null){
            if(SourceStart>=0&&SourceLength>0)AddRange(rectangles,source,SourceStart,SourceLength);
            if(extraStarts.Count>0)AddVisibleExtras(rectangles,source);
        }
        bool changed=rectangles.Count!=geometry.Count;if(!changed)for(int i=0;i<rectangles.Count;i++)if(rectangles[i]!=geometry[i]){changed=true;break;}
        if(changed){geometry=rectangles;InvalidateVisual();}
    }
    // Only the parts laid out as real text are drawn: a folded character's rectangle sits at the
    // fold edge, not at its place.
    void AddRange(List<Rect> rectangles,SourceEditor source,int start,int length){
        if(start<0||length<=0||start>=source.Document.TextLength)return;
        LongLineWindow.RealSpans(source,start,start+length,realSpans);
        for(int s=0;s<realSpans.Count;s+=2)for(int i=realSpans[s];i<realSpans[s+1]&&i<source.Document.TextLength;i++)Add(rectangles,source.GetRectFromCharacterIndex(i,false),source.GetRectFromCharacterIndex(i,true));
    }
    /* Q8: an extra match is drawn only while its line is in the viewport, so a query that matches
       thousands of times still costs the visible ones per layout pass instead of the whole document. */
    void AddVisibleExtras(List<Rect> rectangles,SourceEditor source){
        int viewStart=0,viewEnd=Int32.MaxValue;bool viewKnown=false;
        var view=source.TextArea.TextView;
        if(view!=null&&view.VisualLinesValid&&view.VisualLines.Count>0){
            var lines=view.VisualLines;var document=source.Document;
            /* The TextView keeps its last measured list until the next measure: a document change
               drops the disposed lines from its own list, but this copy stays and a whole-text
               replace deletes the lines it still names. Reading LineNumber from a deleted line
               throws (DocumentLine.LineNumber), so a copy that no longer belongs to this text is
               left out; the LayoutUpdated after the measure calls this again with real lines. */
            var firstLine=lines[0];var lastLine=lines[lines.Count-1];
            if(firstLine.FirstDocumentLine.IsDeleted||lastLine.LastDocumentLine.IsDeleted)return;
            int first=firstLine.FirstDocumentLine.LineNumber,last=lastLine.LastDocumentLine.LineNumber;
            viewStart=document.GetLineByNumber(Math.Max(1,first)).Offset;
            viewEnd=document.GetLineByNumber(Math.Min(document.LineCount,last)).EndOffset;
            viewKnown=true;
        }
        /* Without a laid-out viewport every extra would be drawn (and measured) blindly; the
           LayoutUpdated that follows the next layout pass calls this again with the real lines. */
        if(!viewKnown)return;
        for(int i=0;i<extraStarts.Count;i++){
            int start=extraStarts[i],length=extraLengths[i];
            if(viewKnown&&(start+length<viewStart||start>viewEnd))continue;
            AddRange(rectangles,source,start,length);
            if(rectangles.Count>2000)return;
        }
    }
    protected override void OnRender(DrawingContext drawing){
        RectCount=0;
        drawing.PushClip(new RectangleGeometry(new Rect(0,0,Math.Max(0,AdornedElement.RenderSize.Width-SystemParameters.VerticalScrollBarWidth),Math.Max(0,AdornedElement.RenderSize.Height))));
        var fill=new SolidColorBrush(Color.FromArgb(115,250,204,21));var outline=new Pen(new SolidColorBrush(Color.FromArgb(220,217,119,6)),1);
        foreach(var rectangle in geometry){drawing.DrawRoundedRectangle(fill,outline,rectangle,2,2);RectCount++;}drawing.Pop();
    }
    // Probe seam: the rectangles Reposition computed, before the adorner renders.
    internal int GeometryCount{get{return geometry.Count;}}
    internal Rect GeometryAt(int index){return index>=0&&index<geometry.Count?geometry[index]:Rect.Empty;}
    static void Add(List<Rect> rectangles,Rect left,Rect right){
        if(left.IsEmpty||right.IsEmpty||Double.IsInfinity(left.X)||Double.IsInfinity(right.X))return;
        double width=Math.Abs(right.Right-left.Left);if(Math.Abs(left.Top-right.Top)>Math.Max(left.Height,right.Height)*.6)width=Math.Max(4,left.Width);
        var rectangle=new Rect(Math.Min(left.Left,right.Left),Math.Min(left.Top,right.Top),Math.Max(3,width),Math.Max(left.Height,right.Height));
        if(rectangles.Count>0){var previous=rectangles[rectangles.Count-1];if(Math.Abs(previous.Top-rectangle.Top)<1&&Math.Abs(previous.Height-rectangle.Height)<1&&rectangle.Left<=previous.Right+2){previous.Union(rectangle);rectangles[rectangles.Count-1]=previous;return;}}
        rectangles.Add(rectangle);
    }
}
public partial class MainWindow {
    SearchHighlight sourceHighlight;
    SearchHighlight EnsureSourceHighlight(){
        if(sourceHighlight==null){var layer=AdornerLayer.GetAdornerLayer(source);if(layer!=null){sourceHighlight=new SearchHighlight(source);layer.Add(sourceHighlight);var highlight=sourceHighlight;source.LayoutUpdated+=delegate{highlight.Reposition();};source.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(delegate{highlight.Reposition();}));source.SizeChanged+=delegate{highlight.Reposition();};}}
        return sourceHighlight;
    }
    void UpdateSearchHighlight(SearchMap map,int index){
        var created=EnsureSourceHighlight();
        if(created!=null)created.Clear();
        if(index<0||searchHost.Visibility!=Visibility.Visible)return;
        if(created!=null){created.SourceStart=index;created.SourceLength=QueryText().Length;created.Reposition();}
    }
    /* While the query is still being typed (or composed) no match owns the selection, so the
       current-match range is empty. Mark every match the viewport shows instead. */
    void UpdateSearchHighlightAll(SearchMap map,List<int> matches){
        var highlight=EnsureSourceHighlight();if(highlight==null)return;
        highlight.Clear();
        if(matches==null||matches.Count==0||searchHost.Visibility!=Visibility.Visible)return;
        int cap=Math.Min(matches.Count,4000);
        var starts=new List<int>(cap);var lengths=new List<int>(cap);
        for(int i=0;i<cap;i++){
            int at,length;SourceVisibleRange(map,matches[i],out at,out length);
            if(length>0){starts.Add(at);lengths.Add(length);}
        }
        highlight.SetExtras(starts,lengths);highlight.Reposition();
    }
}
}
