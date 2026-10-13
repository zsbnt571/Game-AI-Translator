using System;
using System.Collections.Generic;

namespace Fusion.UnityEmbedded
{
    // One instance belongs to one live text binding. This observes display
    // changes only: no cache-prefix search, text rewriting or reveal-counter edit.
    internal sealed class IncrementalTextState
    {
        internal const int InitialStableMilliseconds=100;
        internal const int StreamingStableMilliseconds=350;
        internal const int MaximumStableMilliseconds=750;
        internal const int GrowthWindowMilliseconds=1500;
        internal const int MaximumSourceLength=6000;
        string comparison;int comparisonLength;
        long changedAt,lastClock;
        int stableMilliseconds=InitialStableMilliseconds;
        bool initialized,ready,streaming,complete;
        internal bool IsStreaming {get{return streaming;}}
        internal bool IsReady {get{return ready;}}
        internal bool IsAuthoritativeComplete {get{return complete;}}
        internal long StableForMilliseconds {get{return initialized?Math.Max(0,lastClock-changedAt):0;}}
        internal string ExactObservedSource {get{return comparison;}}
        internal string PriorGrowthSource {get;private set;}
        internal int ObservedChanges {get;private set;}
        internal int SuppressedObservations {get;private set;}
        internal int PublishedTransitions {get;private set;}
        internal int RequiredStableMilliseconds {get{return stableMilliseconds;}}

        internal bool Observe(string source,long nowMilliseconds,bool authoritativeComplete)
        {
            PriorGrowthSource=null;
            if(nowMilliseconds<0)nowMilliseconds=0;
            // Monotonic production clocks do not run backwards. A reset clock
            // must not turn a stale pending candidate into a completed one.
            if(initialized&&nowMilliseconds<lastClock)Reset();
            lastClock=nowMilliseconds;
            if(string.IsNullOrEmpty(source)||source.Length>MaximumSourceLength)
            {ClearCandidate();return false;}

            // Some renderers pad the hidden suffix with spaces. Ignore only
            // trailing horizontal padding for progress detection; callers keep
            // the unmodified source for exact lookup, restoration and queuing.
            int end=source.Length;
            while(end>0&&(source[end-1]==' '||source[end-1]=='\t'))end--;
            if(end==0){ClearCandidate();return false;}
            bool changed=!initialized||comparisonLength!=end||string.CompareOrdinal(comparison,0,source,0,end)!=0;
            if(changed)
            {
                long interval=initialized?nowMilliseconds-changedAt:0;
                bool extends=initialized&&end>comparisonLength
                    &&string.CompareOrdinal(source,0,comparison,0,comparisonLength)==0;
                // Any observed extension invalidates earlier reuse evidence,
                // even outside the short window used to detect a typewriter.
                if(extends)PriorGrowthSource=comparison;
                bool grows=extends&&interval<=GrowthWindowMilliseconds;
                if(grows)
                {
                    streaming=true;
                    stableMilliseconds=(int)Math.Min(MaximumStableMilliseconds,
                        Math.Max(StreamingStableMilliseconds,interval*3));
                }
                else stableMilliseconds=streaming?StreamingStableMilliseconds:InitialStableMilliseconds;
                comparison=source;comparisonLength=end;changedAt=nowMilliseconds;initialized=true;ready=complete=false;
                if(ObservedChanges<int.MaxValue)ObservedChanges++;
            }
            // Only a separately verified complete buffer may bypass settling.
            // A cached substring, punctuation mark or component type is not proof.
            if(authoritativeComplete)complete=true;
            bool publish=complete||nowMilliseconds-changedAt>=stableMilliseconds;
            if(publish&&!ready&&PublishedTransitions<int.MaxValue)PublishedTransitions++;
            if(!publish&&SuppressedObservations<int.MaxValue)SuppressedObservations++;
            ready=publish;
            return publish;
        }

        internal void Reset()
        {
            ClearCandidate();changedAt=lastClock=0;
            ObservedChanges=SuppressedObservations=PublishedTransitions=0;
        }
        void ClearCandidate()
        {
            // A cleared/invalid label ends this observation segment. A pooled
            // component may next be a short menu label, not the prior writer.
            comparison=null;comparisonLength=0;initialized=ready=streaming=complete=false;
            PriorGrowthSource=null;
            stableMilliseconds=InitialStableMilliseconds;
        }
    }

    // Session-only, binding-scoped evidence, never populated from a disk cache
    // or a prefix search. A new control receives a new scope even for equal text.
    internal sealed class StableSourceHistory
    {
        internal const int MaximumEntries=2048,MaximumCharacters=524288;
        readonly Dictionary<string,LinkedListNode<string>> entries=new Dictionary<string,LinkedListNode<string>>(StringComparer.Ordinal);
        readonly LinkedList<string> order=new LinkedList<string>();int characters;
        internal int Count {get{return entries.Count;}}
        static string Key(long scope,string source){return scope.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\0"+source;}
        internal bool Contains(long scope,string source){return source!=null&&entries.ContainsKey(Key(scope,source));}
        internal void Remember(long scope,string source)
        {
            if(string.IsNullOrEmpty(source)||source.Length>IncrementalTextState.MaximumSourceLength)return;
            string key=Key(scope,source);if(entries.ContainsKey(key))return;
            while(order.Count>0&&(entries.Count>=MaximumEntries||characters+key.Length>MaximumCharacters))Remove(order.First.Value);
            entries.Add(key,order.AddLast(key));characters+=key.Length;
        }
        internal void Forget(long scope,string source){if(source!=null)Remove(Key(scope,source));}
        void Remove(string key)
        {LinkedListNode<string> node;if(entries.TryGetValue(key,out node)){entries.Remove(key);order.Remove(node);characters-=key.Length;}}
        internal void Clear(){entries.Clear();order.Clear();characters=0;}
    }
}
