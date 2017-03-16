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
        public VimbaDataFrame(IplImage image)
        {
            Image = image;
        }

        public IplImage Image { get; private set; }

        public override string ToString()
        {
            return string.Format("{{Image={0}}}", Image);
        }
    }
}
