using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenCV.Net;
using System.Threading;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using AVT.VmbAPINET;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Bonsai.AlliedVision
{
    public static class RuntimePolicyHelper
    {
        public static bool LegacyV2RuntimeEnabledSuccessfully { get; private set; }

        static RuntimePolicyHelper()
        {
            ICLRRuntimeInfo clrRuntimeInfo =
                (ICLRRuntimeInfo)RuntimeEnvironment.GetRuntimeInterfaceAsObject(
                    Guid.Empty,
                    typeof(ICLRRuntimeInfo).GUID);
            try
            {
                clrRuntimeInfo.BindAsLegacyV2Runtime();
                LegacyV2RuntimeEnabledSuccessfully = true;
            }
            catch (COMException)
            {
                // This occurs with an HRESULT meaning 
                // "A different runtime was already bound to the legacy CLR version 2 activation policy."
                LegacyV2RuntimeEnabledSuccessfully = false;
            }
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BD39D1D2-BA2F-486A-89B0-B4B0CB466891")]
        private interface ICLRRuntimeInfo
        {
            void xGetVersionString();
            void xGetRuntimeDirectory();
            void xIsLoaded();
            void xIsLoadable();
            void xLoadErrorString();
            void xLoadLibrary();
            void xGetProcAddress();
            void xGetInterface();
            void xSetDefaultStartupFlags();
            void xGetDefaultStartupFlags();

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void BindAsLegacyV2Runtime();
        }
    }

    public class VimbaCapture : Source<VimbaDataFrame>
    {
        IObservable<VimbaDataFrame> source;
        readonly object captureLock = new object();
        Camera camera;
        IplImage output;
        bool newFrame = false;

        private void OnFrameReceived(Frame frame)
        {
            unsafe
            {
                var depth = frame.PixelFormat == VmbPixelFormatType.VmbPixelFormatMono16 ? IplDepth.U16 : IplDepth.U8;
                fixed (byte* p = frame.Buffer)
                {
                    IntPtr ptr = (IntPtr)p;
                    var bitmapHeader = new IplImage(new Size((int)frame.Width, (int)frame.Height), depth, 1, ptr);
                    output = new IplImage(bitmapHeader.Size, bitmapHeader.Depth, bitmapHeader.Channels);
                    CV.Copy(bitmapHeader, output);

                    camera.QueueFrame(frame);

                    newFrame = true;
                }
            }
        }

        public VimbaCapture()
        {
            if (RuntimePolicyHelper.LegacyV2RuntimeEnabledSuccessfully)
            {
                source = Observable.Create<VimbaDataFrame>((observer, cancellationToken) =>
                {
                    return Task.Factory.StartNew(() =>
                    {
                        lock (captureLock)
                        {

                            Vimba vimba = new Vimba();
                            vimba.Startup();

                            CameraCollection cameras = vimba.Cameras;

                            string id = cameras[0].Id;

                            camera = vimba.OpenCameraByID(id, VmbAccessModeType.VmbAccessModeFull);
                            camera.OnFrameReceived += OnFrameReceived;

                            try
                            {
                                camera.StartContinuousImageAcquisition(3);

                                while (!cancellationToken.IsCancellationRequested)
                                {

                                    if (newFrame == true)
                                    {
                                        observer.OnNext(new VimbaDataFrame(output));
                                    }
                                }
                            }
                            finally
                            {
                                camera.OnFrameReceived -= OnFrameReceived;
                                camera.StopContinuousImageAcquisition();
                                camera.Close();
                                vimba.Shutdown();
                            }
                        }
                    },
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                })
            .PublishReconnectable()
            .RefCount();
            }
        }

        public int Index { get; set; }

        public override IObservable<VimbaDataFrame> Generate()
        {
            return source;
        }
    }
}
