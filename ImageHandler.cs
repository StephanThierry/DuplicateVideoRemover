using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Enums;
using Xabe.FFmpeg.Streams;

namespace deepduplicates
{
    public class ImageHandler
    {
        public long imageChecksum(string path)
        {
            using (FileStream pngStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                using (var image = new Bitmap(pngStream))
                {
                    using (var graphics = Graphics.FromImage(image))
                    {
                        BitmapData bitmapData = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format16bppRgb555);
                        IntPtr ptr = bitmapData.Scan0;
                        int bytes = Math.Abs(bitmapData.Stride) * image.Height;
                        byte[] rgbValues1 = new byte[bytes];
                        System.Runtime.InteropServices.Marshal.Copy(ptr, rgbValues1, 0, bytes);

                        long profile1 = 0;
                        for (int n = 0; n < rgbValues1.Length; n++) profile1 += rgbValues1[n];
                        
                        // Release the unmanaged memory manually - GC will not do it
                        image.UnlockBits(bitmapData); 
                        return (profile1);
                    }
                }
            }
        }

        public byte[] ImageHashToByteArray(List<bool> imagehash)
        {
            if (imagehash.Count() != 256) return (null); // We are expecting 16x16 

            bool[] imagehash_temp = imagehash.ToArray();
            Array.Reverse(imagehash_temp);
            BitArray bits = new BitArray(imagehash_temp);
            byte[] bytes = new byte[bits.Length / 8];
            bits.CopyTo(bytes, 0);
            return(bytes);
        }

        public List<bool> ByteArrayToImageHash(byte[] imagehashBlob)
        {
            BitArray bits = new BitArray(imagehashBlob);
            bool[] bool_array = new bool[bits.Length];
            bits.CopyTo(bool_array,0);
            List<bool> result = new List<bool>();
            result.AddRange(bool_array);
            return(result);
        }

        public List<bool> ImageHash(string path)
        {
            List<bool> lResult = new List<bool>();

            try
            {
                using (FileStream pngStream = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    using (var image = new Bitmap(pngStream))
                    {
                        //create new image with 16x16 pixel
                        Bitmap bmpMin = new Bitmap(image, new Size(16, 16));
                        for (int j = 0; j < bmpMin.Height; j++)
                        {
                            for (int i = 0; i < bmpMin.Width; i++)
                            {
                                //reduce colors to true / false                
                                lResult.Add(bmpMin.GetPixel(i, j).GetBrightness() < 0.5f);
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
            return lResult;
        }

        public IConversion takeScreenshot(IMediaInfo info, string outputPath, TimeSpan captureTime, int width, int height)
        {

            IVideoStream videoStream = info.VideoStreams.FirstOrDefault()
                                              .SetCodec(VideoCodec.Png)
                                              .SetOutputFramesCount(1)
                                              .SetSeek(captureTime)
                                              .SetScale(new VideoSize(width, height));
            return new Conversion()
                .AddStream(videoStream)
                .SetOutput(outputPath);
        }
    }
}