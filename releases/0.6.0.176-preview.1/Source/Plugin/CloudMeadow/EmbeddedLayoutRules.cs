using System;

namespace Fusion.Embedded.Layout
{
    // Pure decisions for embedded translation only. No Unity, OCR, text mutation,
    // cache identity, engine object, chapter, sample string or screen coordinate.
    internal enum TextUse { Label,Button,Body,Number }
    internal enum LayoutState { Ready,AwaitingInformation,Capacity,InvalidContent }
    internal enum ReadingMode { Direct,NativeFlow,FieldScroll,Unavailable }
    internal struct Extent
    {
        internal float Width,Height;
        internal Extent(float w,float h){Width=w;Height=h;}
    }
    internal struct Area
    {
        internal float Left,Bottom,Right,Top;
        internal Area(float l,float b,float r,float t){Left=l;Bottom=b;Right=r;Top=t;}
        internal float Width{get{return Math.Max(0,Right-Left);}}
        internal float Height{get{return Math.Max(0,Top-Bottom);}}
    }
    internal struct LayoutInput
    {
        internal TextUse Use;
        internal bool Current,RegionReady,MetricsReady,NativeFlow,FieldScroll,KeepGroupSize;
        internal float NativeCap,DisplayCap,ReadableLineHeight,MinimumScaleRatio;
        internal Extent Available;
    }
    internal struct LayoutPlan
    {
        internal LayoutState State;internal ReadingMode Reading;
        internal float Nominal,Scale;internal Extent Measured;
    }
    internal static class EmbeddedLayoutRules
    {
        internal static bool Positive(float n){return n>0&&!float.IsNaN(n)&&!float.IsInfinity(n);}
        internal static bool Fits(Extent a,Extent b)
        {return Positive(a.Width)&&Positive(a.Height)&&a.Width<=b.Width+0.1f&&a.Height<=b.Height+0.1f;}
        internal static LayoutPlan Decide(LayoutInput input,Func<float,Extent> measure)
        {
            var p=new LayoutPlan{State=LayoutState.AwaitingInformation,Reading=ReadingMode.Unavailable,Nominal=1,Scale=1};
            if(!input.Current){p.State=LayoutState.InvalidContent;return p;}
            if(!input.RegionReady||!input.MetricsReady||!Positive(input.NativeCap)||!Positive(input.DisplayCap)||
                !Positive(input.Available.Width)||!Positive(input.Available.Height)||measure==null)return p;
            p.Nominal=p.Scale=input.NativeCap/input.DisplayCap;
            p.Measured=measure(p.Scale);
            if(!Positive(p.Measured.Width)||!Positive(p.Measured.Height))return p;
            if(input.NativeFlow){p.State=LayoutState.Ready;p.Reading=ReadingMode.NativeFlow;return p;}
            if(!Fits(p.Measured,input.Available)&&!input.KeepGroupSize)
            {
                // The adapter supplies a readability policy, never a screenshot ratio.
                float low=p.Nominal*Math.Max(0.85f,Math.Min(1,input.MinimumScaleRatio)),high=p.Nominal;
                if(Fits(measure(low),input.Available))
                {
                    for(int i=0;i<9;i++){float mid=(low+high)*0.5f;if(Fits(measure(mid),input.Available))low=mid;else high=mid;}
                    p.Scale=low;p.Measured=measure(low);
                }
            }
            if(Fits(p.Measured,input.Available)){p.State=LayoutState.Ready;p.Reading=ReadingMode.Direct;return p;}
            p.State=LayoutState.Capacity;
            // A viewport must hold a COMPLETE measured line at the group size.
            // Short labels never get a scroll rail, nor a successful half-line verdict.
            if(input.Use==TextUse.Body&&input.FieldScroll&&Positive(input.ReadableLineHeight)&&input.Available.Height>=input.ReadableLineHeight)
            {p.Scale=p.Nominal;p.Reading=ReadingMode.FieldScroll;}
            return p;
        }
        internal static Area Intersect(Area a,Area b)
        {return new Area(Math.Max(a.Left,b.Left),Math.Max(a.Bottom,b.Bottom),Math.Min(a.Right,b.Right),Math.Min(a.Top,b.Top));}
        internal static Area Avoid(Area allocation,Area obstacle,float gap)
        {
            if(obstacle.Right<=allocation.Left||obstacle.Left>=allocation.Right||obstacle.Top<=allocation.Bottom||obstacle.Bottom>=allocation.Top)return allocation;
            // Keep one continuous owned rectangle; never expand into a neighbour.
            Area best=new Area();float score=0;
            Consider(new Area(allocation.Left,allocation.Bottom,Math.Min(allocation.Right,obstacle.Left-gap),allocation.Top),ref best,ref score);
            Consider(new Area(Math.Max(allocation.Left,obstacle.Right+gap),allocation.Bottom,allocation.Right,allocation.Top),ref best,ref score);
            Consider(new Area(allocation.Left,allocation.Bottom,allocation.Right,Math.Min(allocation.Top,obstacle.Bottom-gap)),ref best,ref score);
            Consider(new Area(allocation.Left,Math.Max(allocation.Bottom,obstacle.Top+gap),allocation.Right,allocation.Top),ref best,ref score);
            return best;
        }
        static void Consider(Area part,ref Area best,ref float score)
        {float area=part.Width*part.Height;if(area>score){score=area;best=part;}}
    }
}
