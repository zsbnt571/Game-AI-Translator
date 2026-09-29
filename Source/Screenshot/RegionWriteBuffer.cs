using System.Collections;

namespace ScreenshotTranslationUiTester;

// Only append and ordered iteration are required by region compositing. Keep
// first-writer ownership without hash-table rehash/copy or large entry arrays.
// One Build owns this object; nothing is shared between tasks or pooled.
internal sealed class RegionWriteBuffer : IEnumerable<KeyValuePair<int,int>>
{
    private const int ChunkSize=4096;
    private BitArray? _written;
    private readonly List<KeyValuePair<int,int>[]> _chunks=[];
    internal int Count { get; private set; }
    internal long BufferBytes=>4L*(((_written?.Length??0)+31)/32)+8L*ChunkSize*_chunks.Count;
    internal RegionWriteBuffer(int pixelCount)=>_written=new BitArray(pixelCount);
    internal bool TryAdd(int pixel,int color)
    {
        if(_written is null)throw new ObjectDisposedException(nameof(RegionWriteBuffer));
        if(_written[pixel])return false;
        if(Count%ChunkSize==0)_chunks.Add(new KeyValuePair<int,int>[ChunkSize]);
        _chunks[Count/ChunkSize][Count%ChunkSize]=new(pixel,color);
        _written[pixel]=true;Count++;return true;
    }
    public IEnumerator<KeyValuePair<int,int>> GetEnumerator()
    {
        for(int i=0;i<Count;i++)yield return _chunks[i/ChunkSize][i%ChunkSize];
    }
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    internal void Release(){_written=null;_chunks.Clear();_chunks.TrimExcess();Count=0;}
}
