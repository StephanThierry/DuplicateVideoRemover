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
        public static byte thumbnailDimensions = 32;
        public rgb[] imageChecksum(string path)
        {
            using (FileStream pngStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                using (var image = new Bitmap(pngStream))
                {
                    using (var graphics = Graphics.FromImage(image))
                    {
                        BitmapData bitmapData = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                        IntPtr ptr = bitmapData.Scan0;
                        int bytes = Math.Abs(bitmapData.Stride) * image.Height;
                        byte[] rgbValues1 = new byte[bytes];
                        System.Runtime.InteropServices.Marshal.Copy(ptr, rgbValues1, 0, bytes);

                        //long profile1 = 0;
                        int nbrOfSegments = 4;
                        int segment = 0;
                        rgb[] profile = new rgb[nbrOfSegments];
                        int pixelPointer = 0;
                        while(pixelPointer+2<bytes){
                            if (profile[segment] == null) profile[segment] = new rgb();
                            profile[segment].r += rgbValues1[pixelPointer];
                            profile[segment].b += rgbValues1[pixelPointer+1];
                            profile[segment].g += rgbValues1[pixelPointer+2];
                            segment = pixelPointer / (bytes / nbrOfSegments);
                            pixelPointer +=3;
                        }
                        for(int i=0;i<nbrOfSegments;i++){
                            List<int> values = new List<int>();
                            values.Add(profile[i].r);
                            values.Add(profile[i].g);
                            values.Add(profile[i].b);
                            int smallestRatio = values.Min() / 100;
                            if(smallestRatio == 0) smallestRatio = 1; 
                            profile[i].r = profile[i].r / smallestRatio;
                            profile[i].g = profile[i].g / smallestRatio;
                            profile[i].b = profile[i].b / smallestRatio;
                        }

                        // Release the unmanaged memory manually - GC will not do it
                        image.UnlockBits(bitmapData);
                        return (profile);
                    }
                }
            }
        }

        public byte[] ImageHashToByteArray(List<bool> imagehash)
        {
            if (imagehash.Count() != thumbnailDimensions * thumbnailDimensions) return (null); // We are expecting an image of size: thumbnailDimensions x thumbnailDimensions 

            bool[] imagehash_temp = imagehash.ToArray();
            Array.Reverse(imagehash_temp);
            BitArray bits = new BitArray(imagehash_temp);
            byte[] bytes = new byte[bits.Length / 8];
            bits.CopyTo(bytes, 0);
            return (bytes);
        }

        public List<bool> ByteArrayToImageHash(byte[] imagehashBlob)
        {
            BitArray bits = new BitArray(imagehashBlob);
            bool[] bool_array = new bool[bits.Length];
            bits.CopyTo(bool_array, 0);
            List<bool> result = new List<bool>();
            result.AddRange(bool_array);
            return (result);
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
                        Bitmap bmpMin = new Bitmap(image, new Size(thumbnailDimensions, thumbnailDimensions));
                        float totalBrightness = 0;
                        for (int j = 0; j < bmpMin.Height; j++)
                        {
                            for (int i = 0; i < bmpMin.Width; i++)
                            {
                                //reduce colors to true / false                
                                totalBrightness += bmpMin.GetPixel(i, j).GetBrightness(); 
                            }
                        }

                        float avgBrightness = totalBrightness/(thumbnailDimensions*thumbnailDimensions);
                        for (int j = 0; j < bmpMin.Height; j++)
                        {
                            for (int i = 0; i < bmpMin.Width; i++)
                            {
                                //reduce colors to true / false                
                                lResult.Add(bmpMin.GetPixel(i, j).GetBrightness() < avgBrightness);
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