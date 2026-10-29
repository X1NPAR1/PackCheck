using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

public sealed record MotdColor(string Code, string Hex);

/// <summary>Server list identity: the 64×64 icon and the two-line MOTD.</summary>
public sealed partial class ServerItemViewModel
{
    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MotdLines), nameof(MotdLength), nameof(MotdTooLong))]
    private string _motdText = string.Empty;

    private string _savedMotd = string.Empty;

    public IReadOnlyList<IReadOnlyList<MotdSpan>> MotdLines => Motd.Parse(Motd.Normalize(MotdText));

    public int MotdLength => Motd.Plain(Motd.Normalize(MotdText)).Length;

    public bool MotdTooLong => MotdLength > Motd.MaxLength;

    public bool HasIcon => Icon is not null;

    public static IReadOnlyList<MotdColor> MotdColors { get; } = [.. Motd.Colors.Select(c => new MotdColor(c.Code.ToString(), c.Hex))];

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));

    private void LoadIdentity()
    {
        Icon = LoadIcon(ServerIcon.PathFor(Definition.Directory));
        var file = Path.Combine(Definition.Directory, "server.properties");
        var values = File.Exists(file) ? ServerProperties.Parse(PropertiesText.ReadFile(file)) : [];
        _savedMotd = values.TryGetValue("motd", out var m) ? PropertiesText.Unescape(m) : "A Minecraft Server";
        MotdText = _savedMotd;
    }

    /// <summary>Loads without locking the file, so it can be replaced while shown.</summary>
    private static BitmapImage? LoadIcon(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Center-crops any image to a square and scales it to 64×64 PNG.</summary>
    internal static byte[] MakeIconPng(byte[] source)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(source), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var side = Math.Min(frame.PixelWidth, frame.PixelHeight);
        var crop = new CroppedBitmap(frame, new System.Windows.Int32Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, side <= 32 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(crop, new System.Windows.Rect(0, 0, ServerIcon.Size, ServerIcon.Size));
        }

        var target = new RenderTargetBitmap(ServerIcon.Size, ServerIcon.Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [RelayCommand]
    private void ChooseIcon()
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("Identity_ImageFilter") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var png = MakeIconPng(File.ReadAllBytes(dlg.FileName));
            File.WriteAllBytes(ServerIcon.PathFor(Definition.Directory), png);
            Icon = LoadIcon(ServerIcon.PathFor(Definition.Directory));
            MainViewModel.Shell?.Toast(IsRunning ? Loc.T("Identity_SavedRestart") : Loc.T("Identity_IconSaved"));
        }
        catch (Exception ex)
        {
            MainViewModel.Shell?.Toast(Loc.T("Identity_ImageError") + " " + ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void RemoveIcon()
    {
        var path = ServerIcon.PathFor(Definition.Directory);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Icon = null;
    }

    [RelayCommand]
    private void InsertMotdCode(string code) => MotdText += "&" + code;

    [RelayCommand]
    private void ResetMotd() => MotdText = _savedMotd;

    [RelayCommand]
    private void SaveMotd()
    {
        if (MotdTooLong)
        {
            MainViewModel.Shell?.Toast(Loc.T("Identity_MotdTooLong"), isError: true);
            return;
        }

        var file = Path.Combine(Definition.Directory, "server.properties");
        var original = File.Exists(file) ? PropertiesText.ReadFile(file) : string.Empty;
        var value = Motd.Normalize(MotdText.Replace("\r", string.Empty));
        File.WriteAllText(file, ServerProperties.Update(original, new Dictionary<string, string> { ["motd"] = PropertiesText.Escape(value) }));
        _savedMotd = value;
        MotdText = value;
        LoadProperties();
        MainViewModel.Shell?.Toast(IsRunning ? Loc.T("Identity_SavedRestart") : Loc.T("Servers_Saved"));
    }
}
