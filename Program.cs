using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;

namespace TestImages
{
    static class Program
    {
        static byte[] _jpegBytes;
        static EventWaitHandle _evt = new AutoResetEvent(false);
        static bool _running = true;

        static void Main(string[] args)
        {
            int imagesOnCollage = int.Parse(args.FirstOrDefault() ?? "10");
            int aCollageDrawingPeriodMs = int.Parse(args.Skip(1).FirstOrDefault() ?? "100");

            //ThreadPool.SetMinThreads(100, 100);

            Console.CancelKeyPress += (s, e) => 
            {
                _running = false;
                e.Cancel = true;
            };

            _jpegBytes = File.ReadAllBytes("image.jpg");

            while (_running)
            {
                var sw = Stopwatch.StartNew();
                DrawCollageImages(imagesOnCollage);
                int spent = (int)sw.Elapsed.TotalMilliseconds;
                int left = aCollageDrawingPeriodMs - spent;
                if (left < 0) left = 0;
                Thread.Sleep(left);
            }

            _evt.Dispose();
        }

        static void DrawCollageImages(int count)
        {
            ThreadPool.QueueUserWorkItem(_ =>
                {
                    Console.WriteLine("thread id {0,4}, threads count {1,3}",
                        Thread.CurrentThread.ManagedThreadId, ThreadPool.ThreadCount);
                    using (var collage = new Bitmap(1000, 1000))
                    {
                        DrawCollageImagesImpl(collage, count);
                    }
                    _evt.Set();
                    Thread.Sleep(20000); // prevent this thread id reuse
                }
            );
            _evt.WaitOne();
        }

        static void DrawCollageImagesImpl(Image collage, int count)
        {
            using (var g = Graphics.FromImage(collage))
            {
                g.DrawCollageImages(count);
            }
        }

        static Rectangle _rect = new Rectangle(0, 0, 500, 500);

        static void DrawCollageImages(this Graphics g, int count)
        {
            for (int i=0; i<count; i++)
            {
                g.DrawCollageImage(_rect);
            }
        }

        static void DrawCollageImage(this Graphics g, Rectangle r)
        {
            using var ms = new MemoryStream(_jpegBytes);
            using var img = Image.FromStream(ms);
            g.DrawImage(img, r.X, r.Y, r.Width, r.Height);
        }
    }
}
