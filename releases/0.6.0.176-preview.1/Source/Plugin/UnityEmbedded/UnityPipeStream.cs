using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Fusion.UnityEmbedded
{
    // Unity's legacy System.Core can report version 3.5 without implementing
    // System.IO.Pipes. Keep that optional framework type out of the plugin.
    internal sealed class UnityPipeStream : Stream
    {
        readonly SafeFileHandle handle;
        volatile bool disposed;
        UnityPipeStream(SafeFileHandle handle){this.handle=handle;}

        internal static UnityPipeStream Connect(string name,int timeoutMilliseconds,Func<bool> cancelled)
        {
            if(string.IsNullOrEmpty(name)||name.Length>200||name.IndexOfAny(new[]{'\\','/','\0'})>=0)throw new ArgumentException("Invalid local pipe name.","name");
            if(timeoutMilliseconds<0)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            string path="\\\\.\\pipe\\"+name;var watch=Stopwatch.StartNew();
            while(true)
            {
                if(cancelled())throw new OperationCanceledException("Unity pipe connection cancelled.");
                // Overlapped I/O permits disposal to cancel reads and writes. The
                // server gets identification, never an impersonation token.
                var opened=CreateFile(path,0xc0000000,0,IntPtr.Zero,3,0x40110000,IntPtr.Zero);
                if(!opened.IsInvalid)return new UnityPipeStream(opened);
                int error=Marshal.GetLastWin32Error();opened.Dispose();
                if(error!=2&&error!=231)throw NativeError("connect",error);
                long remaining=timeoutMilliseconds-watch.ElapsedMilliseconds;
                if(remaining<=0)throw new TimeoutException("Unity pipe connection timed out.");
                if(error==231)WaitNamedPipe(path,(uint)Math.Min(50,remaining));
                else Thread.Sleep((int)Math.Min(20,remaining));
            }
        }

        public override bool CanRead{get{return !disposed;}}
        public override bool CanWrite{get{return !disposed;}}
        public override bool CanSeek{get{return false;}}
        public override long Length{get{throw new NotSupportedException();}}
        public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
        public override void SetLength(long value){throw new NotSupportedException();}
        public override void Flush(){if(disposed)throw new ObjectDisposedException("UnityPipeStream");}
        public override int Read(byte[] buffer,int offset,int count){Validate(buffer,offset,count);return count==0?0:Transfer(buffer,offset,count,false);}
        public override void Write(byte[] buffer,int offset,int count)
        {
            Validate(buffer,offset,count);
            while(count>0){int written=Transfer(buffer,offset,count,true);if(written==0)throw new IOException("Unity pipe write returned no bytes.");offset+=written;count-=written;}
        }
        void Validate(byte[] buffer,int offset,int count)
        {
            if(disposed)throw new ObjectDisposedException("UnityPipeStream");
            if(buffer==null)throw new ArgumentNullException("buffer");
            if(offset<0||count<0||offset>buffer.Length-count)throw new ArgumentOutOfRangeException("offset");
        }
        int Transfer(byte[] buffer,int offset,int count,bool write)
        {
            bool retained=false,ioPending=false;GCHandle pinned=default(GCHandle);IntPtr overlapped=IntPtr.Zero,native=IntPtr.Zero;
            using(var completed=new ManualResetEvent(false))
            {
                try
                {
                    handle.DangerousAddRef(ref retained);
                    if(disposed)throw new ObjectDisposedException("UnityPipeStream");
                    native=handle.DangerousGetHandle();
                    pinned=GCHandle.Alloc(buffer,GCHandleType.Pinned);
                    var state=new OverlappedData{Event=completed.SafeWaitHandle.DangerousGetHandle()};
                    overlapped=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(OverlappedData)));
                    Marshal.StructureToPtr(state,overlapped,false);
                    IntPtr data=new IntPtr(pinned.AddrOfPinnedObject().ToInt64()+offset);uint transferred=0;bool success=false;int error=0;
                    // Legacy CLR defers Thread.Abort in a finally: issuing I/O and
                    // recording its state must not be separated by that exception.
                    try{}
                    finally
                    {
                        success=write?WriteFile(native,data,(uint)count,out transferred,overlapped):ReadFile(native,data,(uint)count,out transferred,overlapped);
                        error=success?0:Marshal.GetLastWin32Error();ioPending=!success&&error==997;
                    }
                    if(ioPending)
                    {
                        // Do not free the pinned buffer or OVERLAPPED until Windows
                        // has completed a cancellation as well as normal I/O.
                        while(!completed.WaitOne(50))if(disposed)CancelIoEx(native,overlapped);
                        success=GetOverlappedResult(native,overlapped,out transferred,false);
                        error=success?0:Marshal.GetLastWin32Error();
                        ioPending=!success&&error==996;
                    }
                    if(!success)
                    {
                        if(disposed)throw new ObjectDisposedException("UnityPipeStream");
                        if(!write&&(error==109||error==232||error==233))return 0;
                        throw NativeError(write?"write":"read",error);
                    }
                    return checked((int)transferred);
                }
                finally
                {
                    // Interrupted waits must also drain cancellation before the
                    // kernel loses access to its OVERLAPPED, event or buffer.
                    if(ioPending)
                    {
                        CancelIoEx(native,overlapped);uint ignored;
                        GetOverlappedResult(native,overlapped,out ignored,true);
                    }
                    if(overlapped!=IntPtr.Zero)Marshal.FreeHGlobal(overlapped);
                    if(pinned.IsAllocated)pinned.Free();
                    if(retained)handle.DangerousRelease();
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if(!disposed)
            {
                disposed=true;bool retained=false;
                try{handle.DangerousAddRef(ref retained);CancelIoEx(handle.DangerousGetHandle(),IntPtr.Zero);}
                catch(ObjectDisposedException){}
                finally{if(retained)handle.DangerousRelease();handle.Dispose();}
            }
            base.Dispose(disposing);
        }
        static IOException NativeError(string operation,int error){return new IOException("Unity pipe "+operation+" failed (Win32 "+error+").",new Win32Exception(error));}
        [StructLayout(LayoutKind.Sequential)]struct OverlappedData{internal IntPtr Internal,InternalHigh;internal uint Offset,OffsetHigh;internal IntPtr Event;}
        [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",EntryPoint="WaitNamedPipeW",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool WaitNamedPipe(string name,uint timeout);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool ReadFile(IntPtr handle,IntPtr buffer,uint count,out uint read,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool WriteFile(IntPtr handle,IntPtr buffer,uint count,out uint written,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetOverlappedResult(IntPtr handle,IntPtr overlapped,out uint transferred,[MarshalAs(UnmanagedType.Bool)]bool wait);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool CancelIoEx(IntPtr handle,IntPtr overlapped);
    }
}
