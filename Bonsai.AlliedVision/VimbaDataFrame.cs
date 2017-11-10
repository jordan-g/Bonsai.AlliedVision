using OpenCV.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AVT.VmbAPINET;

namespace Bonsai.AlliedVision
{
    public class VimbaDataFrame
    {
        public VimbaDataFrame(IplImage image, UInt64 vimbaTimestamp, UInt64 frameID)
        {
            Image = image;
            VimbaTimestamp = vimbaTimestamp;
            FrameID = frameID;
        }

        public IplImage Image { get; private set; }

        public UInt64 VimbaTimestamp { get; private set; }

        public UInt64 FrameID { get; private set; }

        public override string ToString()
        {
            return string.Format("{{Image={0}}}", Image);
        }
    }
}
