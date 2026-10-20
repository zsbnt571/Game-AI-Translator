namespace ScreenshotTranslationUiTester;

internal sealed partial class NativeGameLibraryGrid
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
    // Ownership moves between the active tiles and this bounded inactive LRU.
    // A -> B -> A navigation must not decode A again or dispose visible pixels.
    private readonly LinkedList<(string Key,LibraryTile Tile,long Bytes)> inactiveImages=[];
    private readonly Dictionary<string,LinkedListNode<(string Key,LibraryTile Tile,long Bytes)>> inactiveImageKeys=[];
    private long inactiveImageBytes;
    private static string ImageKey(RecentGame game)=>string.Join("\0",game.Id,game.ArtworkVersion,game.Cover,game.Icon,game.IconStamp);
    private void CacheInactive(LibraryTile tile)
    {
        string key=ImageKey(tile.Game);long bytes=ImageBytes(tile);
        if(inactiveImageKeys.ContainsKey(key)||bytes>RetainedImageBudget){tile.Dispose();return;}
        while(inactiveImages.Last is {} last&&(inactiveImages.Count>=128||inactiveImageBytes+bytes>RetainedImageBudget))
        {inactiveImageBytes-=last.Value.Bytes;inactiveImageKeys.Remove(last.Value.Key);inactiveImages.RemoveLast();last.Value.Tile.Dispose();}
        inactiveImageKeys[key]=inactiveImages.AddFirst((key,tile,bytes));inactiveImageBytes+=bytes;
    }
    private bool TakeInactive(RecentGame game,out LibraryTile? tile)
    {
        tile=null;if(!inactiveImageKeys.Remove(ImageKey(game),out var node))return false;
        inactiveImages.Remove(node);inactiveImageBytes-=node.Value.Bytes;tile=node.Value.Tile;return true;
    }
    private void DisposeInactiveImages(){foreach(var entry in inactiveImages)entry.Tile.Dispose();inactiveImages.Clear();inactiveImageKeys.Clear();inactiveImageBytes=0;}
    internal bool ImageLoadCompleted(RecentGame game)=>imagesLoaded.Contains(game.Id);
    internal async Task PrepareVisibleImagesAsync(LibraryTile[] next,Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)> loader,Point desiredOffset)
    {
        int columns=ShelfMode?1:Columns;
        int stride=ShelfMode?Cell.Width+S(12):Cell.Height+S(12);
        int first=Math.Max(0,(ShelfMode?desiredOffset.X:desiredOffset.Y-(pageScrollEnabled?pageLeadingHeight:0))/Math.Max(1,stride))*columns;
        int count=Math.Clamp(((ShelfMode?ViewportSize.Width:ViewportSize.Height)/Math.Max(1,stride)+2)*columns,1,64);
        var active=tiles.Where(t=>imagesLoaded.Contains(t.Game.Id)).Select(t=>ImageKey(t.Game)).ToHashSet(StringComparer.Ordinal);
        var work=Enumerable.Range(Math.Min(first,next.Length),Math.Min(count,Math.Max(0,next.Length-first)))
            .Where(i=>next[i].Cover is null&&next[i].Icon is null&&!active.Contains(ImageKey(next[i].Game))&&!inactiveImageKeys.ContainsKey(ImageKey(next[i].Game))).ToArray();
        // No dispatcher mutation until all first-screen decodes complete.
        await Task.Run(()=>{foreach(int i in work){try{var image=loader(next[i].Game);next[i]=next[i] with{Cover=image.Cover,Icon=image.Icon};}catch{}}});
    }
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
        for(int i=0;i<tiles.Length;i++)
        {
            var prior=tiles[i];if(prior.Cover is null&&prior.Icon is null)continue;
            CacheInactive(prior);tiles[i]=prior with{Cover=null,Icon=null};
        }
        for(int i=0;i<next.Length;i++)
        {
            if(imagesLoaded.Contains(next[i].Game.Id))continue;
            if(next[i].Cover is null&&next[i].Icon is null&&TakeInactive(next[i].Game,out var cached))next[i]=next[i] with{Cover=cached!.Cover,Icon=cached.Icon};
            if(next[i].Cover is null&&next[i].Icon is null)continue;
            imagesLoaded.Add(next[i].Game.Id);loadedImageIndices.Add(i);imageByteSizes[i]=ImageBytes(next[i]);retainedImageBytes+=imageByteSizes[i];
        }
    }

    // Decode only nearby images. Keep recently viewed ones within a bounded LRU
    // cache so reversing a scroll does not repeatedly decode/upload the covers.
    private async void QueueTileImages()
    {
        if(imagesLoading||imageLoader is null||IsDisposed||wpfCanvas?.IsLoaded!=true)return;
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
