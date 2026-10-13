namespace ScreenshotTranslationUiTester;

internal sealed partial class GameLibraryGrid
{
    private Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)>? imageLoader;
    private int imageEpoch;
    private bool imagesLoading;
    private readonly HashSet<string> imagesLoaded=[];
    private void ResetTileImages(Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)>? loader)
    {imageEpoch++;imageLoader=loader;imagesLoaded.Clear();imagesLoading=false;}

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
        }
    }

    // All records remain navigable; only nearby covers are read and retained.
    private async void QueueTileImages()
    {
        if(imagesLoading||imageLoader is null||IsDisposed||!IsHandleCreated)return;
        var view=Rectangle.Inflate(ContentViewport,Cell.Width,Cell.Height);
        var nearby=Enumerable.Range(0,tiles.Length).Where(i=>BoundsFor(i).IntersectsWith(view)).Take(64).ToArray();
        var keep=nearby.ToHashSet();
        for(int i=0;i<tiles.Length;i++)
            if(!keep.Contains(i)&&imagesLoaded.Remove(tiles[i].Game.Id))
            {var old=tiles[i];tiles[i]=old with{Cover=null,Icon=null};old.Dispose();}
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
            foreach(var item in ready){tiles[item.Index]=item.Tile;imagesLoaded.Add(item.Tile.Game.Id);}
            Invalidate();
        }
        finally{if(epoch==imageEpoch)imagesLoading=false;}
    }
}
