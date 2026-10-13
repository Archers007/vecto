using SixLabors.ImageSharp;
using Vecto.Core;
using ISRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace Vecto.Cli;

public static class ImageIo
{
    public static RasterImage Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // JPEG: decode like libjpeg-turbo / WIC so the CLI traces the same pixels as the app (ImageSharp's IDCT and
        // chroma upsampling differ); files outside the decoder's scope fall through to ImageSharp
        if (JpegDecoder.IsJpeg(bytes) && JpegDecoder.TryDecode(bytes) is { } jpeg)
            return jpeg;
        using var image = Image.Load<ISRgba32>(bytes);
        var buf = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(buf);
        return new RasterImage(image.Width, image.Height, buf);
    }

    public static void SavePng(RasterImage img, string path)
    {
        using var image = Image.LoadPixelData<ISRgba32>(img.Pixels, img.Width, img.Height);
        image.SaveAsPng(path);
    }
}
