using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ScreenshotTranslationUiTester;

public sealed record ImageHashDiagnostics(int ImageHashCount,long ImageHashTotalMs,int PngEncodeForHashCount,
    long RawPixelHashMs,long BytesSerializedForHash,long BytesHashed,string HashMethod);

public sealed class SessionImageIdentity
{
    private readonly object _gate=new();
    private readonly Bitmap _image;
    private Task<string>? _hashTask;
    private int _hashCount;
    private long _hashMs;
    private long _bytesHashed;

    public SessionImageIdentity(Bitmap image)=>_image=image;
    public Task? ComputationTask { get { lock(_gate)return _hashTask; } }
    public ImageHashDiagnostics Diagnostics { get { lock(_gate)return new(_hashCount,_hashMs,0,_hashMs,0,_bytesHashed,"SHA256 normalized BGRA32 raw pixels"); } }

    public async Task<string> GetHashAsync(CancellationToken cancellationToken=default)
    {
        Task<string> task;
        lock(_gate)task=_hashTask??=Task.Run(ComputeRawPixelHash);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ComputeRawPixelHash()
    {
        var watch=Stopwatch.StartNew();var bytes=0L;
        try
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> metadata=stackalloc byte[12];
            BitConverter.TryWriteBytes(metadata[..4],_image.Width);
            BitConverter.TryWriteBytes(metadata.Slice(4,4),_image.Height);
            BitConverter.TryWriteBytes(metadata.Slice(8,4),(int)PixelFormat.Format32bppArgb);
            hash.AppendData(metadata);
            var rect=new Rectangle(0,0,_image.Width,_image.Height);
            var data=_image.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes=checked(_image.Width*4);var row=new byte[rowBytes];
                for(var y=0;y<_image.Height;y++)
                {
                    var sourceY=data.Stride<0?_image.Height-1-y:y;
                    Marshal.Copy(data.Scan0+sourceY*data.Stride,row,0,rowBytes);
                    hash.AppendData(row);bytes+=rowBytes;
                }
            }
            finally{_image.UnlockBits(data);}
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            watch.Stop();lock(_gate){_hashCount++;_hashMs+=watch.ElapsedMilliseconds;_bytesHashed+=bytes;}
        }
    }
}
