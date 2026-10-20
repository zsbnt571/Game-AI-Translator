using System.Windows.Media.Imaging;
namespace ScreenshotTranslationUiTester;

// Frozen sources may be retained by a visible hero after LRU eviction. No GDI
// bitmap is shared or disposed underneath a view. Only successful decodes stay.
internal sealed class WpfCoverCache(long capacityBytes=192L*1024*1024,int capacityCount=48)
{
    private sealed record Entry(string Key,BitmapSource Image,long Bytes);
    private readonly object gate=new();
    private readonly LinkedList<Entry> recent=[];
    private readonly Dictionary<string,LinkedListNode<Entry>> entries=[];
    private readonly Dictionary<string,Task<BitmapSource?>> pending=[];
    private long bytes;
    private int generation;
    internal long RetainedBytes {get{lock(gate)return bytes;}}
    internal bool TryGet(string? key,out BitmapSource? image)
    {
        lock(gate)
        {
            image=null;if(key is null||!entries.TryGetValue(key,out var node))return false;
            recent.Remove(node);recent.AddFirst(node);image=node.Value.Image;return true;
        }
    }
    internal void Remember(string key,BitmapSource image)
    {
        if(!image.IsFrozen)throw new InvalidOperationException("Cover sources must be frozen.");
        lock(gate)
        {
            if(entries.ContainsKey(key))return;
            long cost=(long)image.PixelWidth*image.PixelHeight*4;
            if(cost>capacityBytes)return;
            while(recent.Count>0&&(bytes+cost>capacityBytes||recent.Count>=capacityCount))
            {var last=recent.Last!;bytes-=last.Value.Bytes;entries.Remove(last.Value.Key);recent.RemoveLast();}
            entries[key]=recent.AddFirst(new Entry(key,image,cost));bytes+=cost;
        }
    }
    internal Task<BitmapSource?> GetAsync(string? key,Func<BitmapSource?> load)
    {
        if(key is null)return Task.FromResult<BitmapSource?>(null);
        lock(gate)
        {
            if(TryGet(key,out var image))return Task.FromResult(image);
            if(pending.TryGetValue(key,out var loading))return loading;
            int epoch=generation;
            var task=Task.Run(()=>
            {
                var value=load();
                if(value is not null&&!value.IsFrozen)throw new InvalidOperationException("Cover sources must be frozen.");
                lock(gate)
                {
                    if(epoch!=generation||value is null)return value;
                    long cost=(long)value.PixelWidth*value.PixelHeight*4;
                    if(cost>capacityBytes)return value;
                    while(recent.Count>0&&(bytes+cost>capacityBytes||recent.Count>=capacityCount))
                    {var last=recent.Last!;bytes-=last.Value.Bytes;entries.Remove(last.Value.Key);recent.RemoveLast();}
                    entries[key]=recent.AddFirst(new Entry(key,value,cost));bytes+=cost;
                }
                return value;
            });
            pending[key]=task;
            _=task.ContinueWith(_=>{lock(gate){if(pending.TryGetValue(key,out var current)&&ReferenceEquals(current,task))pending.Remove(key);}},TaskScheduler.Default);
            return task;
        }
    }
    internal void Clear(){lock(gate){generation++;entries.Clear();recent.Clear();pending.Clear();bytes=0;}}
}

public sealed partial class MainForm
{
    private readonly WpfCoverCache _libraryCoverCache=new();
    private BitmapSource? _libraryArtworkSource;
    // The visible crop belongs to this decoded image, not to the newest store
    // record (which can change before an asynchronous image decode finishes).
    private RecentGame? _libraryArtworkRecord;
    internal BitmapSource? WpfSessionArtworkSource=>_libraryArtworkSource??(GameCoverImage is Bitmap bitmap?WpfDrawing.Image(bitmap):null);
}
