namespace NetVips.Benchmarks
{
    using System;
    using System.IO;
    using BenchmarkDotNet.Attributes;

    using Image = NetVips.Image;

    using ImageMagick;

    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.Processing;
    using ImageSharpImage = SixLabors.ImageSharp.Image;
    using ImageSharpRectangle = SixLabors.Primitives.Rectangle;
    using ImageSharpSize = SixLabors.Primitives.Size;

    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Drawing.Imaging;
    using SystemDrawingImage = System.Drawing.Image;
    using SystemDrawingRectangle = System.Drawing.Rectangle;

    using SkiaSharp;

    [Config(typeof(Config))]
    public class Benchmark
    {
        [GlobalSetup]
        public void GlobalSetup()
        {
            // Turn off OpenCL acceleration
            OpenCL.IsEnabled = false;
        }

        [Benchmark(Description = "NetVips", Baseline = true)]
        [Arguments("t.tif", "t2.tif")]
        [Arguments("t.jpg", "t2.jpg")]
        public void NetVips(string input, string output)
        {
            var im = Image.NewFromFile(input, access: Enums.Access.Sequential);

            im = im.Crop(100, 100, im.Width - 200, im.Height - 200);
            im = im.Reduce(1.0 / 0.9, 1.0 / 0.9, kernel: Enums.Kernel.Linear);
            var mask = Image.NewFromArray(new[,]
            {
                {-1, -1, -1},
                {-1, 16, -1},
                {-1, -1, -1}
            }, 8);
            im = im.Conv(mask, precision: Enums.Precision.Integer);

            im.WriteToFile(output);
        }

        [Benchmark(Description = "Magick.NET")]
        [Arguments("t.tif", "t2.tif")]
        [Arguments("t.jpg", "t2.jpg")]
        public void MagickNet(string input, string output)
        {
            using (var im = new MagickImage(input))
            {
                im.Shave(100, 100);
                im.Resize(new Percentage(90.0));

                // All values in the kernel are divided by 8 (to match libvips scale behavior)
                var kernel = new ConvolveMatrix(3, -0.125, -0.125, -0.125, -0.125, 2, -0.125, -0.125, -0.125, -0.125);
                im.Convolve(kernel);

                im.Write(output);
            }
        }

        [Benchmark(Description = "ImageSharp")]
        [Arguments("t.jpg", "t2.jpg")] // ImageSharp doesn't have TIFF support
        public void ImageSharp(string input, string output)
        {
            using (var image = ImageSharpImage.Load(input))
            {
                image.Mutate(x => x
                    .Crop(new ImageSharpRectangle(100, 100, image.Width - 200, image.Height - 200))
                    .Resize(new ImageSharpSize((int)Math.Round(image.Width * .9F),
                        (int)Math.Round(image.Height * .9F)))
                    .GaussianSharpen(.75f));

                image.Save(output);
            }
        }

        [Benchmark(Description = "SkiaSharp")]
        [Arguments("t.jpg", "t2.jpg")] // SkiaSharp doesn't have TIFF support
        public void SkiaSharp(string input, string output)
        {
            using (var bitmap = SKBitmap.Decode(input))
            {
                bitmap.ExtractSubset(bitmap, SKRectI.Create(100, 100, bitmap.Width - 200, bitmap.Height - 200));

                var targetWidth = (int)Math.Round(bitmap.Width * .9F);
                var targetHeight = (int)Math.Round(bitmap.Height * .9F);

                using (var resized = bitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKFilterQuality.High))
                {
                    using (var surface = SKSurface.Create(new SKImageInfo(targetWidth, targetHeight, bitmap.ColorType,
                        bitmap.AlphaType)))
                    using (var canvas = surface.Canvas)
                    using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
                    {
                        var kernel = new[]
                        {
                            -1f, -1f, -1f,
                            -1f, 16f, -1f,
                            -1f, -1f, -1f
                        };
                        var kernelSize = new SKSizeI(3, 3);
                        var kernelOffset = new SKPointI(1, 1);

                        paint.ImageFilter = SKImageFilter.CreateMatrixConvolution(kernelSize, kernel, 0.125f, 0f,
                            kernelOffset, SKMatrixConvolutionTileMode.Repeat, false);

                        canvas.DrawBitmap(resized, 0, 0, paint);
                        canvas.Flush();

                        using (var fileStream = File.OpenWrite(output))
                        {
                            surface.Snapshot()
                                // The default quality of 85 usually produces excellent results
                                .Encode(SKEncodedImageFormat.Jpeg, 85)
                                .SaveTo(fileStream);
                        }
                    }
                }
            }
        }

        [Benchmark(Description = "System.Drawing")]
        [Arguments("t.jpg", "t2.jpg")]
        [Arguments("t.tif", "t2.tif")]
        public void SystemDrawing(string input, string output)
        {
            using (var image = SystemDrawingImage.FromFile(input, true))
            {
                var cropRect = new SystemDrawingRectangle(100, 100, image.Width - 200, image.Height - 200);
                var resizeRect = new SystemDrawingRectangle(0, 0, (int)Math.Round(cropRect.Width * .9F),
                    (int)Math.Round(cropRect.Height * .9F));

                using (var src = new Bitmap(cropRect.Width, cropRect.Height))
                {
                    using (var cropGraphics = Graphics.FromImage(src))
                    {
                        cropGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        cropGraphics.CompositingMode = CompositingMode.SourceCopy;
                        cropGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        // Crop
                        cropGraphics.DrawImage(image, new SystemDrawingRectangle(0, 0, src.Width, src.Height),
                            cropRect, GraphicsUnit.Pixel);

                        // Dispose early, since we don't need it anymore
                        image.Dispose();
                    }

                    using (var resized = new Bitmap(resizeRect.Width, resizeRect.Height))
                    using (var resizeGraphics = Graphics.FromImage(resized))
                    using (var attributes = new ImageAttributes())
                    {
                        // Get rid of the annoying artifacts
                        attributes.SetWrapMode(WrapMode.TileFlipXY);

                        resizeGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        resizeGraphics.CompositingMode = CompositingMode.SourceCopy;
                        resizeGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        // Resize
                        resizeGraphics.DrawImage(src, resizeRect, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel,
                            attributes);

                        // No sharpening or convolution operation seems to be available

                        resized.Save(output);
                    }
                }
            }
        }
    }
}
