using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using ImageMagick;

namespace TestImages
{
    static class Program
    {
        static byte[] _jpegBytes;

        static void Main(string[] args)
        {
            var url = "https://www.fnordware.com/superpng/pnggradHDrgba.png";

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent: Other");
                _jpegBytes = client.DownloadData(url);
            }

            long completed = 0;
            long exceptions = 0;
            long outstanding = 0;
            long max = 8;
            var start = DateTime.Now;

            while (true)
            {
                while (outstanding < max)
                {
                    Interlocked.Increment(ref outstanding);

                    ThreadPool.QueueUserWorkItem((_) =>
                    {
                        try
                        {
                            Repro();
                            Interlocked.Increment(ref completed);
                        }
                        catch(Exception ex)
                        {
                            Console.WriteLine(ex);
                            Interlocked.Increment(ref exceptions);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref outstanding);
                        }
                    });

                    var elapsed = DateTime.Now - start;

                    if (elapsed.Seconds > 5)
                    {
                        ThreadPool.GetAvailableThreads(out int wt, out int _);
                        Console.WriteLine($"Completed: {completed}, Exceptions: {exceptions}, Outstanding: {outstanding}, ThreadCount={ThreadPool.ThreadCount}, avail={wt}, cwi={ThreadPool.CompletedWorkItemCount}, pwi={ThreadPool.PendingWorkItemCount}");
                        start = DateTime.Now;
                    }
                }
            }
        }

        private static void Repro()
        {
            using (var ms = new MemoryStream(_jpegBytes))
            {
                using (var resampled = ResampleTextureEnforceDesiredSizeWithPadding(ms, 512, 512))
                {
                    ReadToEnd(resampled);
                }
            }
        }

        private static void ReadToEnd(Stream s)
        {
            var buf = new byte[65536];
            int total = 0;
            int read = 0;

            while((read = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += read;
            }

            s.Seek(0, SeekOrigin.Begin);
        }

        public static Stream ResampleTextureEnforceDesiredSizeWithPadding(Stream texture, int desiredWidth, int desiredHeight)
        {
            var destAspectRatio = desiredWidth / (float)desiredHeight;

            using (var scaledBitmap = new Bitmap(desiredWidth, desiredHeight, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(scaledBitmap))
                {
                    var attr = new ImageAttributes();
                    attr.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    using (var sourceImage = ImageFromStream(texture))
                    {
                        float srcAspectRatio = sourceImage.Width / (float)sourceImage.Height;
                        float relativeAspectRatio = srcAspectRatio / destAspectRatio;

                        if (relativeAspectRatio >= 1.0)
                        {
                            g.DrawImage(sourceImage, new Rectangle(0, 0, desiredWidth, (int)(desiredHeight / relativeAspectRatio)), 0, 0, sourceImage.Width, sourceImage.Height, GraphicsUnit.Pixel, attr);
                        }
                        else
                        {
                            g.DrawImage(sourceImage, new Rectangle((int)(desiredWidth * (1.0 - relativeAspectRatio)) / 2, 0, (int)(desiredWidth * relativeAspectRatio), desiredHeight), 0, 0, sourceImage.Width, sourceImage.Height, GraphicsUnit.Pixel, attr);
                        }
                    }
                }

                var memoryStream = new MemoryStream();
                scaledBitmap.Save(memoryStream, ImageFormat.Png);
                memoryStream.Seek(0, SeekOrigin.Begin);
                return memoryStream;
            }
        }

        private const int StreamBufferSize = 1024;
        private const int TgaFooterSize = 26;

        /// <summary>
        /// Determines whether a byte Stream constitutes TGA image data or not.
        /// 
        /// This works by paging to the end of the stream and looking for the TGA 2.0
        /// footer. If the TGA is 1.0 this method will not work, and there is no 100%
        /// foolproof way of determining if the data is TGA or not.
        /// </summary>
        /// <param name="texture">The Image stream.</param>
        /// <returns>True if the data stream contains the TGA footer tag.</returns>
        public static bool IsStreamTga(Stream imageStream)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException("imageStream");
            }

            // This would be so much simpler if texture.Seek would work
            // on any offset but 0 with the file upload streams. 
            // It does not.

            // we page through with two buffers so the footer doesn't get truncated
            byte[][] buffer = { new byte[StreamBufferSize], new byte[StreamBufferSize] };
            int lastCount;
            int totalCount = 0;
            int index = 1;
            do
            {
                lastCount = imageStream.Read(buffer[index - 1], 0, StreamBufferSize);
                index = 3 - index;
                totalCount++;
            } while (lastCount == StreamBufferSize);

            // reset the stream so MagickImage can start fresh
            imageStream.Seek(0, SeekOrigin.Begin);

            // if the total file size is less than the tga footer size, it mustn't have a footer
            if (totalCount == 1 && lastCount < TgaFooterSize)
            {
                return false;
            }

            // put our two buffers together in the right order
            // note if the file size <= buffer size we'll stick an empty buffer
            // in the front, which is technically in the wrong order,
            // but the offset calculation does the right thing so it works out
            var streamTail = buffer[index - 1].Concat(buffer[(3 - index) - 1]).ToArray();

            // grab the footer data and check for the tga tag
            byte[] footerBuffer = new byte[TgaFooterSize];
            var footerOffset = StreamBufferSize + lastCount - TgaFooterSize;
            Array.Copy(streamTail, footerOffset, footerBuffer, 0, TgaFooterSize);
            // despite the specification the TRUEVISION tag is not always at
            // index -18 from the end of the file (some files append a null as 
            // they should, but some don't) so we check the whole signature
            var signature = Encoding.UTF8.GetString(footerBuffer);
            if (signature.IndexOf("TRUEVISION-XFILE", StringComparison.Ordinal) > -1)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Converts the incompatible format (such as TGA) to a PNG the .Net library can use.
        /// </summary>
        /// <param name="imageStream">The image stream.</param>
        /// <returns>A new stream or null if the Stream is compatible</returns>
        /// <exception cref="System.ArgumentNullException">imageStream</exception>
        public static Stream ConvertIncompatibleFormatToPng(Stream imageStream)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException("imageStream");
            }

            Stream pngStream = null;
            if (IsStreamTga(imageStream))
            {
                pngStream = TgaToPng(imageStream);
            }
            return pngStream;
        }

        /// <summary>
        /// Converts a TGA data stream to a PNG data stream.
        /// </summary>
        /// <param name="imageStream">The image stream to convert.</param>
        /// <returns>
        /// A PNG data stream.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">imageStream</exception>
        /// <exception cref="System.InvalidOperationException">Unrecognized image format</exception>
        public static Stream TgaToPng(Stream imageStream)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException("imageStream");
            }

            var memoryStream = new MemoryStream();
            var magickSettings = new MagickReadSettings { Format = MagickFormat.Tga };
            magickSettings.SetDefine(MagickFormat.Png, "exclude-chunks", "date");
            try
            {
                using (var sourceImage = new MagickImage(imageStream, magickSettings))
                {
                    sourceImage.Write(memoryStream, MagickFormat.Png);
                }

            }
            catch (MagickException me)
            {
                if (me is MagickMissingDelegateErrorException
                    || me is MagickCorruptImageErrorException)
                {
                    throw new InvalidOperationException("Failure to parse image: " + me.Message);
                }

                throw;
            }
            memoryStream.Seek(0, SeekOrigin.Begin);
            imageStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        }

        /// <summary>
        /// Turns a stream into an image, making sure the stream is one .NET's image code can handle
        /// </summary>
        /// <param name="imageStream">The image stream.</param>
        /// <returns>An image derived from the stream.</returns>
        /// <exception cref="System.ArgumentNullException">imageStream</exception>
        public static Image ImageFromStream(Stream imageStream)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException("imageStream");
            }

            Image image = null;
            using (var stream = ConvertIncompatibleFormatToPng(imageStream))
            {
                image = Image.FromStream(stream ?? imageStream);
            }
            return image;
        }
    }
}
