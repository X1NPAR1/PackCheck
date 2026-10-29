using System.IO;
using System.Windows.Media.Imaging;

namespace PackCheck.App.Ai;

/// <summary>A file waiting to be sent: an image (sent to the model as a picture) or text (added to the message).</summary>
public sealed record ChatAttachment(string Name, bool IsImage, byte[] Data, string MediaType, string? Text)
{
    public string SizeText => Data.Length >= 1024 * 1024 ? $"{Data.Length / 1024d / 1024:0.0} MB" : $"{Math.Max(1, Data.Length / 1024)} KB";

    /// <summary>Small preview for the chips above the input.</summary>
    public BitmapSource? Thumbnail => IsImage ? AttachmentLoader.Decode(Data, 96) : null;
}

/// <summary>Reads files, pasted images and dropped files into attachments with size limits that suit chat models.</summary>
public static class AttachmentLoader
{
    public const int MaxAttachments = 6;
    public const int MaxImageSide = 1568;
    public const int MaxTextChars = 120_000;
    public const long MaxFileBytes = 20 * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    private static readonly string[] TextExtensions =
    [
        ".txt", ".log", ".yml", ".yaml", ".json", ".json5", ".properties", ".toml", ".cfg", ".conf", ".ini", ".xml", ".md", ".csv",
        ".js", ".sk", ".mcmeta", ".mcfunction", ".snbt", ".bat", ".cmd", ".sh", ".ps1", ".lang",
    ];

    public static string FileFilter => "*" + string.Join(";*", ImageExtensions.Concat(TextExtensions));

    public static bool IsImageName(string name) => ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext) || TextExtensions.Contains(ext);
    }

    /// <summary>Loads a file; throws <see cref="InvalidDataException"/> with a user-facing reason key when it cannot be attached.</summary>
    public static ChatAttachment FromFile(string path)
    {
        var info = new FileInfo(path);
        if (!IsSupported(path))
        {
            throw new InvalidDataException("Attach_Unsupported");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException("Attach_TooLarge");
        }

        var bytes = File.ReadAllBytes(path);
        return ImageExtensions.Contains(info.Extension.ToLowerInvariant()) ? FromImageBytes(info.Name, bytes) : FromText(info.Name, bytes);
    }

    public static ChatAttachment FromText(string name, byte[] bytes)
    {
        var text = PackCheck.Servers.PropertiesText.Decode(bytes);
        if (text.Length > MaxTextChars)
        {
            // Logs matter most at the end.
            text = "… (" + (text.Length - MaxTextChars).ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters cut)\n" + text[^MaxTextChars..];
        }

        return new ChatAttachment(name, false, bytes, "text/plain", text);
    }

    /// <summary>Keeps PNG/JPEG/GIF/WebP as they are when small; larger or other images are scaled down and saved as PNG (screenshots) or JPEG (photos).</summary>
    public static ChatAttachment FromImageBytes(string name, byte[] bytes)
    {
        var frame = DecodeFrame(bytes) ?? throw new InvalidDataException("Attach_BadImage");
        var media = MediaTypeOf(bytes);
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (media is not null && longest <= MaxImageSide && bytes.Length <= 3_500_000)
        {
            return new ChatAttachment(name, true, bytes, media, null);
        }

        return FromBitmap(name, frame);
    }

    /// <summary>A pasted screenshot or a decoded image, scaled to at most <see cref="MaxImageSide"/> pixels.</summary>
    public static ChatAttachment FromBitmap(string name, BitmapSource source)
    {
        BitmapSource bmp = source;
        var longest = Math.Max(bmp.PixelWidth, bmp.PixelHeight);
        if (longest > MaxImageSide)
        {
            var scale = (double)MaxImageSide / longest;
            bmp = new TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(scale, scale));
        }

        byte[] Encode(BitmapEncoder enc)
        {
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        var png = Encode(new PngBitmapEncoder());
        if (png.Length <= 3_500_000)
        {
            return new ChatAttachment(Path.ChangeExtension(name, ".png"), true, png, "image/png", null);
        }

        var jpg = Encode(new JpegBitmapEncoder { QualityLevel = 85 });
        return new ChatAttachment(Path.ChangeExtension(name, ".jpg"), true, jpg, "image/jpeg", null);
    }

    /// <summary>Media type from the file's first bytes (only the formats chat models accept).</summary>
    public static string? MediaTypeOf(byte[] b) =>
        b.Length < 12 ? null
        : b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
        : b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b[0] == 'G' && b[1] == 'I' && b[2] == 'F' ? "image/gif"
        : b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "image/webp"
        : null;

    public static BitmapSource? Decode(byte[] bytes, int width)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = width;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static BitmapFrame? DecodeFrame(byte[] bytes)
    {
        try
        {
            return BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
