namespace ScreenshotTranslationUiTester;

internal sealed partial class GameLibraryGrid
{
    private Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)>? imageLoader;
    private int imageEpoch;
    private bool imagesLoading;
    private readonly HashSet<string> imagesLoaded=[];
    private readonly HashSet<int> loadedImageIndices=[];
    private readonly Dictionary<int,long> imageUse=[];
    private readonly Dictionary<int,long> imageByteSizes=[];
    private int[] lastImageWindow=[];
    private long retainedImageBytes;
    private long imageUseClock;
    private const long RetainedImageBudget = 64L * 1024 * 1024;
    private static long ImageBytes(LibraryTile tile) => (tile.Cover is { } cover ? (long)cover.Width*cover.Height*4 : 0) + (tile.Icon is { } icon ? (long)icon.Width*icon.Height*4 : 0);
    private void ResetTileImages(Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)>? loader)
    {imageEpoch++;imageLoader=loader;imagesLoaded.Clear();loadedImageIndices.Clear();imageUse.Clear();imageByteSizes.Clear();lastImageWindow=[];retainedImageBytes=0;imagesLoading=false;}

    private void RetainTileImages(LibraryTile[] next,Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)>? loader)
    {
        var previouslyLoaded=imagesLoaded.ToHashSet();
        ResetTileImages(loader);
        if(loader is null)return;
        var byId=tiles.Select((tile,index)=>(tile,index)).ToDictionary(x=>x.tile.Game.Id);
        for(int i=0;i<next.Length;i++)
        {
            if(!byId.TryGetValue(next[i].Game.Id,out var prior)||!previouslyLoaded.Contains(prior.tile.Game.Id))continue;
            var a=prior.tile.Game;var b=next[i].Game;
            if(a.ArtworkVersion!=b.ArtworkVersion||a.Cover!=b.Cover||a.Icon!=b.Icon||a.IconStamp!=b.IconStamp)continue;
            if(next[i].Cover is not null||next[i].Icon is not null)continue;
            next[i]=next[i] with{Cover=prior.tile.Cover,Icon=prior.tile.Icon};
            // Transfer ownership; the old tile array can now be safely disposed.
            tiles[prior.index]=prior.tile with{Cover=null,Icon=null};
            imagesLoaded.Add(b.Id);
            loadedImageIndices.Add(i);
            imageByteSizes[i]=ImageBytes(next[i]);retainedImageBytes+=imageByteSizes[i];
        }
    }

    // Decode only nearby images. Keep recently viewed ones within a bounded LRU
    // cache so reversing a scroll does not repeatedly decode/upload the covers.
    private async void QueueTileImages()
    {
        if(imagesLoading||imageLoader is null||IsDisposed||!IsHandleCreated)return;
        var view=Rectangle.Inflate(ContentViewport,Cell.Width,Cell.Height);
        var nearby=VisibleTileIndices(view).Take(64).ToArray();
        // A scroll frame often stays inside the same preload rows. Bitmap size
        // getters enter GDI+, so never recount every cached image on each frame.
        if(nearby.AsSpan().SequenceEqual(lastImageWindow))return;
        lastImageWindow=nearby;
        var keep=nearby.ToHashSet();
        foreach(int i in nearby)imageUse[i]=++imageUseClock;
        TrimImages(keep);
        var work=nearby.Where(i=>!imagesLoaded.Contains(tiles[i].Game.Id)).Select(i=>(Index:i,Tile:tiles[i])).ToArray();
        if(work.Length==0)return;
        int epoch=imageEpoch;var loader=imageLoader;imagesLoading=true;
        try
        {
            var ready=await Task.Run(()=>work.Select(item=>
            {
                try{var images=loader(item.Tile.Game);return (item.Index,Tile:item.Tile with{Cover=images.Cover,Icon=images.Icon});}
                catch{return (item.Index,item.Tile);}
            }).ToArray());
            if(IsDisposed||epoch!=imageEpoch){foreach(var item in ready)item.Tile.Dispose();return;}
            foreach(var item in ready)
            {
                retainedImageBytes-=imageByteSizes.GetValueOrDefault(item.Index);
                tiles[item.Index]=item.Tile;imagesLoaded.Add(item.Tile.Game.Id);loadedImageIndices.Add(item.Index);
                imageByteSizes[item.Index]=ImageBytes(item.Tile);retainedImageBytes+=imageByteSizes[item.Index];
            }
            TrimImages(keep);
            Invalidate();
        }
        finally{if(epoch==imageEpoch)imagesLoading=false;}
    }
    private void TrimImages(HashSet<int> keep)
    {
        if(retainedImageBytes<=RetainedImageBudget&&loadedImageIndices.Count<=128)return;
        foreach(int i in loadedImageIndices.Where(i=>!keep.Contains(i)).OrderBy(i=>imageUse.GetValueOrDefault(i)).ToArray())
        {
            var old=tiles[i];retainedImageBytes-=imageByteSizes.GetValueOrDefault(i);imageByteSizes.Remove(i);loadedImageIndices.Remove(i);imagesLoaded.Remove(old.Game.Id);imageUse.Remove(i);tiles[i]=old with{Cover=null,Icon=null};old.Dispose();
            if(retainedImageBytes<=RetainedImageBudget&&loadedImageIndices.Count<=128)break;
        }
    }
}
