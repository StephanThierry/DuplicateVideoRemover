using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Enums;
using Xabe.FFmpeg.Streams;

namespace deepduplicates
{

    // Uses SkiaSharp (cross-platform) instead of System.Drawing, which only works on Windows in .NET 6+.
    public class ImageHandler
    {
        public static byte thumbnailDimensions = 32;

        public rgb[] imageChecksum(string path)
        {
            byte[] rgbValues1 = ReadAs24bppBgr(path, out int bytes);

            // Same algorithm as before, on the same bytes (24-bit, B-G-R order, rows padded
            // to 4 bytes, like System.Drawing's Format24bppRgb), so results match existing DB values.
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
            return (profile);
        }

        /// <summary>Decodes an image into the byte layout System.Drawing used for Format24bppRgb.</summary>
        private static byte[] ReadAs24bppBgr(string path, out int bytes)
        {
            using (var decoded = SKBitmap.Decode(path))
            {
                if (decoded == null) throw new ArgumentException("Can't decode image: " + path);
                using (var bitmap = decoded.Copy(SKColorType.Rgba8888))
                {
                    int width = bitmap.Width, height = bitmap.Height;
                    int stride = ((width * 3) + 3) & ~3;
                    bytes = stride * height;
                    byte[] result = new byte[bytes];
                    ReadOnlySpan<byte> src = bitmap.GetPixelSpan();
                    int srcStride = bitmap.RowBytes;
                    for (int y = 0; y < height; y++)
                    {
                        int s = y * srcStride, d = y * stride;
                        for (int x = 0; x < width; x++, s += 4, d += 3)
                        {
                            result[d] = src[s + 2];     // B
                            result[d + 1] = src[s + 1]; // G
                            result[d + 2] = src[s];     // R
                        }
                    }
                    return result;
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
                using (var image = SKBitmap.Decode(path))
                using (var bmpMin = image.Resize(new SKImageInfo(thumbnailDimensions, thumbnailDimensions, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                                                 new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
                {
                    float totalBrightness = 0;
                    for (int j = 0; j < bmpMin.Height; j++)
                    {
                        for (int i = 0; i < bmpMin.Width; i++)
                        {
                            //reduce colors to true / false                
                            totalBrightness += GetBrightness(bmpMin.GetPixel(i, j)); 
                        }
                    }

                    float avgBrightness = totalBrightness/(thumbnailDimensions*thumbnailDimensions);
                    for (int j = 0; j < bmpMin.Height; j++)
                    {
                        for (int i = 0; i < bmpMin.Width; i++)
                        {
                            //reduce colors to true / false                
                            lResult.Add(GetBrightness(bmpMin.GetPixel(i, j)) < avgBrightness);
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

        // Same formula as System.Drawing.Color.GetBrightness()
        private static float GetBrightness(SKColor c)
        {
            int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            return (max + min) / (byte.MaxValue * 2f);
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