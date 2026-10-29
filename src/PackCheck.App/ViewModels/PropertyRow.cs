using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;

namespace PackCheck.App.ViewModels;

/// <summary>One server.properties entry with type-aware editing, validation and reset.</summary>
public sealed partial class PropertyRow : ObservableObject
{
    private readonly string _original;

    public PropertyRow(string key, string value, bool inFile)
    {
        Key = key;
        Info = PropertyCatalog.Find(key);
        _original = value;
        _value = value;
        InFile = inFile;
    }

    public PropertyRow(PropertyInfo info, string value)
        : this(info.Key, value, inFile: false)
    {
        Info = info;
    }

    public string Key { get; }

    public string DisplayKey => Info?.IsYaml == true ? $"{Info.File} › {Info.Path}" : Key;

    public PropertyInfo? Info { get; }

    public bool InFile { get; }

    public PropertyKind Kind => Info?.Kind ?? PropertyKind.Text;

    public string GroupKey => Info?.Group ?? "Other";

    public IReadOnlyList<string> Options => Info?.Options ?? [];

    public string Description => Info is null ? string.Empty : Loc.T("Prop_" + Key);

    public string DefaultText => Info is null ? string.Empty : Loc.F("Props_Default", Info.Default.Length == 0 ? "–" : Info.Default);

    public bool IsSecret => Info?.Secret == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsDefault), nameof(BoolValue), nameof(Error))]
    private string _value;

    /// <summary>Changed since the file was loaded (unsaved).</summary>
    public bool IsChanged => Value != _original;

    public bool IsDefault => Info is null || Value == Info.Default;

    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    public string? Error
    {
        get
        {
            if (Kind != PropertyKind.Number || Info is null)
            {
                return null;
            }

            return int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= Info.Min && n <= Info.Max
                ? null
                : Loc.F("Props_Range", Info.Min, Info.Max);
        }
    }

    /// <summary>Whether this row must be written: present in the file, or changed by the user.</summary>
    public bool ShouldWrite => Info?.IsYaml == true ? IsChanged : InFile || IsChanged;

    [RelayCommand]
    private void Reset()
    {
        if (Info is not null)
        {
            Value = Info.Default;
        }
    }

    public bool Matches(string search) =>
        search.Length == 0
        || DisplayKey.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(search, StringComparison.OrdinalIgnoreCase);
}

public sealed record PropertyGroup(string Key, ObservableCollection<PropertyRow> Rows)
{
    public string Title => Loc.T("PropGroup_" + Key);
}
