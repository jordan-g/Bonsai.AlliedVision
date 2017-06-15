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
using System.ComponentModel;
using System.Diagnostics;

namespace Bonsai.AlliedVision
{
    public static class RuntimePolicyHelper
    {
        // Enable legacy V2 runtime (to work properly with the Allied Vision SDK)
        // Taken from: http://reedcopsey.com/2011/09/15/setting-uselegacyv2runtimeactivationpolicy-at-runtime/

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

    // Set the node's description
    [Description("Produces a sequence of images acquired from an Allied Vision camera using the Vimba SDK.")]
    public class VimbaCapture : Source<VimbaDataFrame>
    {
        // Create camera index parameter
        [Description("The index of the camera from which to acquire images.")]
        public int Index { get; set; }

        public override IObservable<VimbaDataFrame> Generate()
        {
            return source;
        }

        // Create exposure time parameter
        bool exposureTimeChanged;
        private double exposureTime;
        [Description("Exposure time (ms). This controls the maximum framerate.")]
        public double ExposureTime
        {
            get
            {
                return exposureTime;
            }
            set
            {
                exposureTime = Math.Round(value, 2);
                exposureTimeChanged = true;
            }
        }
        
        // Create frame rate parameter
        bool frameRateChanged;
        private double frameRate;
        [Description("Desired frame rate (frames / s). This is superceded by the maximum framerate allowed by the exposure time.")]
        public double FrameRate
        {
            get
            {
                return frameRate;
            }
            set
            {
                frameRate = Math.Floor(value * 100) / 100;
                frameRateChanged = true;
            }
        }

        // Create black level parameter
        bool blackLevelChanged;
        private double blackLevel;
        [Description("Black level (DN). This controls the analog black level as DC offset applied to the video signal.")]
        public double BlackLevel
        {
            get
            {
                return blackLevel;
            }
            set
            {
                blackLevel = Math.Round(value, 2);
                blackLevelChanged = true;
            }
        }

        // Create gain parameter
        bool gainChanged;
        private double gain;
        [Description("Adjusts the gain (dB). This controls the gain as an amplification factor applied to the video signal.")]
        public double Gain
        {
            get
            {
                return gain;
            }
            set
            {
                gain = Math.Round(value, 2);
                gainChanged = true;
            }
        }

        // Create gamma parameter
        bool gammaChanged;
        private double gamma;
        [Description("Adjusts the gamma (dB). This controls the gamma correction of pixel intensity.")]
        public double Gamma
        {
            get
            {
                return gamma;
            }
            set
            {
                gamma = Math.Round(value, 2);
                gammaChanged = true;
            }
        }

        // Create parameter for determining whether to automatically use the maximum possible frame rate for the current exposure time
        private bool useMaxFrameRate;
        [Description("Whether to set the FPS to the maximum possible value based on the current exposure time.")]
        public bool UseMaxFrameRate
        {
            get
            {
                return useMaxFrameRate;
            }
            set
            {
                useMaxFrameRate = value;
                frameRateChanged = true;
            }
        }

        // Create variables
        IObservable<VimbaDataFrame> source;
        readonly object captureLock = new object();
        Camera camera;
        IplImage output;
        Size frameSize;
        IplDepth depth;
        Stopwatch timer = new Stopwatch();
        Int64 frequency = Stopwatch.Frequency;
        IObserver<VimbaDataFrame> global_observer;
        double defaultExposureTime;
        double defaultFrameRate;
        double defaultBlackLevel;
        double defaultGain;
        double defaultGamma;
        double maxFrameRate;
        double realMaxFrameRate;

        // Callback function for when a frame is grabbed by the camera
        private void OnFrameReceived(Frame frame)
        {
            if (VmbFrameStatusType.VmbFrameStatusComplete == frame.ReceiveStatus)
            {
                unsafe
                {
                    fixed (byte* p = frame.Buffer)
                    {
                        // Timestamp the frame
                        Int64 timestamp = timer.ElapsedTicks;

                        // Copy the frame into an IplImage object
                        var bitmapHeader = new IplImage(frameSize, depth, 1, (IntPtr)p);
                        output = new IplImage(bitmapHeader.Size, bitmapHeader.Depth, bitmapHeader.Channels);
                        CV.Copy(bitmapHeader, output);

                        // Send to the next node
                        global_observer.OnNext(new VimbaDataFrame(output, timestamp, frequency, frame.FrameID));

                        // Queue the frame object for receiving new data
                        camera.QueueFrame(frame);
                    }
                }
            }
        }

        // Function to increase the frame rate until the Allied Vision API throws an error.
        // NOTE: This function is used to quickly set the maximum possible frame rate for the current exposure time.
        private double increaseFrameRateUntilError(FeatureCollection features, double stepSize, double startingFrameRate)
        {
            bool end = false;

            double fps = startingFrameRate;

            while (fps <= 1000 && end == false)
            {
                try
                {
                    features["AcquisitionFrameRate"].FloatValue = fps;
                    fps = fps + stepSize;
                }
                catch
                {
                    end = true;
                }
            }

            return fps;
        }

        // Function to decrease the frame rate until the Allied Vision API stops throwing an error.
        // NOTE: This function is used to quickly set the maximum possible frame rate for the current exposure time.
        private double decreaseFrameRateUntilNoError(FeatureCollection features, double stepSize, double startingFrameRate)
        {
            bool end = false;

            double fps = startingFrameRate;

            while (fps > 0 && end == false)
            {
                try
                {
                    features["AcquisitionFrameRate"].FloatValue = fps;
                    end = true;
                }
                catch
                {
                    fps = fps - stepSize;
                }
            }

            return fps;
        }

        // Function to get the real maximum possible frame rate for the current exposure time.
        // NOTE: The theoretical maximum frame rate (maxFrameRate) does not take into account latencies
        //       between frames. This function starts at the theoretical max and adjusts the frame rate
        //       to find the real limit.
        private double getRealMaxFrameRate(FeatureCollection features, double maxFrameRate)
        {
            // Start the fps at the theoretical maximum frame rate that is given
            double fps = maxFrameRate;

            // Decrease the fps until we reach the real limit
            fps = decreaseFrameRateUntilNoError(features, 0.1, fps);
            fps = increaseFrameRateUntilError(features, 0.01, fps) - 0.01;

            return fps;
        }

        // Function to get the maximum possible frame rate for the camera
        private double getCameraFrameRateLimit(FeatureCollection features)
        {
            // Save the current exposure time & fps
            double oldExposureTime = features["ExposureTime"].FloatValue;
            double oldFPS = features["AcquisitionFrameRate"].FloatValue;

            // Start the fps at 30
            double fps = 30;

            // Set the exposure time to be very high -- 1000 us (1 ms)
            features["ExposureTime"].FloatValue = 1000.0;

            // Increase the fps until we reach the limit
            fps = increaseFrameRateUntilError(features, 10, fps);
            fps = decreaseFrameRateUntilNoError(features, 1, fps);
            fps = increaseFrameRateUntilError(features, 0.1, fps);
            fps = decreaseFrameRateUntilNoError(features, 0.01, fps);

            // Reset the exposure time & fps
            features["ExposureTime"].FloatValue = oldExposureTime;
            features["AcquisitionFrameRate"].FloatValue = oldFPS;

            return fps;
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
                            global_observer = observer;

                            // Start the Vimba API
                            Vimba vimba = new Vimba();
                            vimba.Startup();

                            // Get a list of the connected cameras
                            CameraCollection cameras = vimba.Cameras;

                            // Get the ID of the camera at index given by the Index parameter
                            string id = cameras[Index].Id;

                            // Open the camera
                            camera = vimba.OpenCameraByID(id, VmbAccessModeType.VmbAccessModeFull);

                            // Set the callback function for when a frame is received
                            camera.OnFrameReceived += new Camera.OnFrameReceivedHandler(OnFrameReceived);
                            
                            try
                            {
                                // Get the camera's features
                                FeatureCollection features = camera.Features;

                                // Set the acquisition mode
                                features["AcquisitionFrameRateMode"].EnumValue = "Basic";

                                // Calculate the camera's maximum possible fps
                                double cameraFrameRateLimit = getCameraFrameRateLimit(features);

                                if (ExposureTime == 0)
                                {
                                    // Set exposure time variable to current value on the camera (convert from us to ms)
                                    ExposureTime = features["ExposureTime"].FloatValue / 1000.0;

                                    // Get the theoretical maximum framerate for this exposure time
                                    maxFrameRate = Math.Min(1000.0 / ExposureTime, cameraFrameRateLimit);

                                    // Set the fps variable to the real maximum
                                    realMaxFrameRate = getRealMaxFrameRate(features, maxFrameRate);
                                    FrameRate = realMaxFrameRate;
                                }

                                // Set black level parameter to the camera's current black level setting
                                if (BlackLevel == 0)
                                {
                                    BlackLevel = features["BlackLevel"].FloatValue;
                                }

                                // Set gain parameter to the camera's current gain setting
                                if (Gain == 0)
                                {
                                    Gain = features["Gain"].FloatValue;
                                }

                                // Set gamma parameter to the camera's current gamma setting
                                if (Gamma == 0)
                                {
                                    Gamma = features["Gamma"].FloatValue;
                                }

                                // Set frameSize & depth features
                                frameSize = new Size((int)features["Width"].IntValue, (int)features["Height"].IntValue);
                                depth = IplDepth.U8;

                                // Start capturing frames
                                camera.StartContinuousImageAcquisition(3);

                                // Start a timer for timestamping frames
                                timer.Start();

                                while (!cancellationToken.IsCancellationRequested)
                                {
                                    if (exposureTimeChanged)
                                    {
                                        // User changed the exposure time parameter.
                                        // Set the exposure time feature on the camera.
                                        features["ExposureTime"].FloatValue = ExposureTime * 1000.0;

                                        exposureTimeChanged = false;

                                        // Adjust the framerate given this new exposure time
                                        frameRateChanged = true;
                                    }

                                    if (frameRateChanged)
                                    {
                                        // Get the theoretical maximum framerate for the current exposure time
                                        maxFrameRate = Math.Min(1000.0 / ExposureTime, cameraFrameRateLimit);

                                        // Get the real maximum framerate
                                        realMaxFrameRate = getRealMaxFrameRate(features, maxFrameRate);

                                        if (!UseMaxFrameRate)
                                        {
                                            // Limit the current frame rate to be less than or equal to the real maximum
                                            FrameRate = Math.Min(FrameRate, realMaxFrameRate);
                                        }
                                        else
                                        {
                                            // Set the frame rate to the real maximum
                                            FrameRate = realMaxFrameRate;
                                        }

                                        // Set the framerate feature
                                        features["AcquisitionFrameRate"].FloatValue = FrameRate;

                                        frameRateChanged = false;
                                    }

                                    if (blackLevelChanged)
                                    {
                                        // User changed the black level parameter.
                                        // Set the black level feature on the camera.
                                        features["BlackLevel"].FloatValue = BlackLevel;

                                        blackLevelChanged = false;
                                    }

                                    if (gainChanged)
                                    {
                                        // User changed the gain parameter.
                                        // Set the gain feature on the camera.
                                        features["Gain"].FloatValue = Gain;

                                        gainChanged = false;
                                    }

                                    if (gammaChanged)
                                    {
                                        // User changed the gamma parameter.
                                        // Set the gamma feature on the camera.
                                        features["Gamma"].FloatValue = Gamma;

                                        gammaChanged = false;
                                    }
                                }
                            }
                            finally
                            {
                                // Stop everything
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
    }
}
