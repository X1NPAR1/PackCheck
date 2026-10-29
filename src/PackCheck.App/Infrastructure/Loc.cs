using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace PackCheck.App.Infrastructure;

/// <summary>Runtime-switchable localization. XAML binds to Loc.Instance[key]; changing Culture refreshes every binding.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages =
    [
        ("en", "English"), ("tr", "Türkçe"), ("de", "Deutsch"), ("fr", "Français"), ("ru", "Русский"),
    ];

    private static readonly ResourceManager Resources = new("PackCheck.App.Localization.Strings", typeof(Loc).Assembly);

    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CultureChanged;

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (Equals(_culture, value))
            {
                return;
            }

            _culture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            ApplyXamlLanguage();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// XAML formats dates and numbers (StringFormat=g, N0 …) with the element's Language, which is en-US unless set.
    /// Every window gets the UI culture so "29.10.2026 18:12" shows in Turkish instead of "10/29/2026 6:12 PM".
    /// </summary>
    public static void ApplyXamlLanguage(System.Windows.FrameworkElement? element = null)
    {
        var lang = XmlLanguage.GetLanguage(Instance._culture.IetfLanguageTag);
        if (element is not null)
        {
            element.Language = lang;
            return;
        }

        if (System.Windows.Application.Current is { } app)
        {
            foreach (System.Windows.Window w in app.Windows)
            {
                w.Language = lang;
            }
        }
    }

    public string this[string key] => Resources.GetString(key, _culture) ?? $"#{key}";

    public static string T(string key) => Instance[key];

    public bool Has(string key) => Resources.GetString(key, _culture) is not null;

    public static string F(string key, params object[] args) => string.Format(Instance._culture, Instance[key], args);
}

/// <summary>{l:Loc Key} markup extension.</summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
