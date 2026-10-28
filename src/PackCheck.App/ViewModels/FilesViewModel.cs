using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Document;
using PackCheck.App.Infrastructure;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

public sealed partial class FileEntry(string fullPath, bool isDirectory, long size, DateTime modified) : ObservableObject
{
    public string FullPath { get; } = fullPath;

    public string Name => Path.GetFileName(FullPath);

    public bool IsDirectory { get; } = isDirectory;

    public long Size { get; } = size;

    public DateTime Modified { get; } = modified;

    public string Kind => IsDirectory ? "folder" : Path.GetExtension(FullPath).TrimStart('.').ToLowerInvariant();

    public string SizeText => IsDirectory ? string.Empty : (string)Bytes.Instance.Convert(Size, typeof(string), null, Loc.Instance.Culture);

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record Crumb(string Name, string Path);

/// <summary>An open file in the code editor.</summary>
public sealed partial class EditorTab : ObservableObject
{
    public EditorTab(string path, string text, bool readOnly)
    {
        FullPath = path;
        Document = new TextDocument(text);
        ReadOnly = readOnly;
        Document.TextChanged += (_, _) => IsDirty = true;
    }

    public string FullPath { get; }

    public string Name => Path.GetFileName(FullPath);

    public TextDocument Document { get; }

    public bool ReadOnly { get; }

    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private int _errorLine;
}

/// <summary>File manager for one server folder. Every path is checked to stay inside the server root.</summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private const long MaxEditableBytes = 5L * 1024 * 1024;
    private static readonly string[] TextExtensions = [".yml", ".yaml", ".json", ".json5", ".toml", ".properties", ".txt", ".log", ".cfg", ".conf", ".ini", ".xml", ".js", ".sh", ".bat", ".cmd", ".md", ".csv", ".mcmeta", ".secret", ".lang"];
    private static readonly string[] RunningLocked = ["world", "world_nether", "world_the_end", "libraries", "versions"];

    private readonly ServerItemViewModel _server;
    private readonly List<string> _clipboard = [];
    private bool _clipboardIsCut;

    public FilesViewModel(ServerItemViewModel server)
    {
        _server = server;
        Root = Path.GetFullPath(server.Definition.Directory);
        _current = Root;
    }

    public string Root { get; }

    public ObservableCollection<FileEntry> Entries { get; } = [];

    public ObservableCollection<Crumb> Crumbs { get; } = [];

    public ObservableCollection<EditorTab> Tabs { get; } = [];

    [ObservableProperty]
    private string _current;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private EditorTab? _activeTab;

    [ObservableProperty]
    private int _selectedCount;

    public bool HasClipboard => _clipboard.Count > 0;

    public bool HasTabs => Tabs.Count > 0;

    partial void OnFilterChanged(string value) => Refresh();

    /// <summary>Resolves a path and refuses anything outside the server folder (including "..", links and other drives).</summary>
    public string Confine(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Current, path));
        var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Path is outside the server folder.");
        }

        return full;
    }

    private bool IsLocked(string path)
    {
        if (!_server.IsBusy)
        {
            return false;
        }

        var rel = Path.GetRelativePath(Root, path).Split(Path.DirectorySeparatorChar)[0];
        return RunningLocked.Contains(rel, StringComparer.OrdinalIgnoreCase) || path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    public void Refresh()
    {
        Entries.Clear();
        if (!Directory.Exists(Current))
        {
            Current = Root;
        }

        var dir = new DirectoryInfo(Current);
        var items = dir.EnumerateDirectories().Select(d => new FileEntry(d.FullName, true, 0, d.LastWriteTime))
            .Concat(dir.EnumerateFiles().Select(f => new FileEntry(f.FullName, false, f.Length, f.LastWriteTime)))
            .Where(e => Filter.Length == 0 || e.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var e in items)
        {
            e.PropertyChanged += (_, a) =>
            {
                if (a.PropertyName == nameof(FileEntry.IsSelected))
                {
                    SelectedCount = Entries.Count(x => x.IsSelected);
                }
            };
            Entries.Add(e);
        }

        SelectedCount = 0;
        Crumbs.Clear();
        Crumbs.Add(new Crumb(_server.Name, Root));
        var rel = Path.GetRelativePath(Root, Current);
        if (rel != ".")
        {
            var acc = Root;
            foreach (var part in rel.Split(Path.DirectorySeparatorChar))
            {
                acc = Path.Combine(acc, part);
                Crumbs.Add(new Crumb(part, acc));
            }
        }
    }

    private List<FileEntry> Selection() => Entries.Where(e => e.IsSelected).ToList();

    private void Fail(Exception ex) => MainViewModel.Shell?.Toast(ex.Message, isError: true);

    [RelayCommand]
    private void Navigate(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Current = Confine(path);
            Filter = string.Empty;
            Refresh();
        }
        catch (UnauthorizedAccessException ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private void Up()
    {
        if (!Current.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            Navigate(Path.GetDirectoryName(Current));
        }
    }

    [RelayCommand]
    private void Open(FileEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            Navigate(entry.FullPath);
        }
        else
        {
            OpenInEditor(entry.FullPath);
        }
    }

    public static bool IsText(string path) =>
        TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || Path.GetFileName(path) is "eula.txt" or "server.properties" or "ops.json";

    public void OpenInEditor(string path)
    {
        try
        {
            var full = Confine(path);
            if (Tabs.FirstOrDefault(t => t.FullPath.Equals(full, StringComparison.OrdinalIgnoreCase)) is { } open)
            {
                ActiveTab = open;
                return;
            }

            if (!IsText(full))
            {
                MainViewModel.Shell?.Toast(Loc.T("Files_NotText"), isError: true);
                return;
            }

            var info = new FileInfo(full);
            var readOnly = info.Length > MaxEditableBytes;
            string text;
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                if (readOnly)
                {
                    // Large logs open read-only and only the first 5 MB is shown, so the UI stays responsive.
                    var buffer = new char[MaxEditableBytes];
                    text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
                }
                else
                {
                    text = reader.ReadToEnd();
                }
            }

            var tab = new EditorTab(full, text, readOnly);
            tab.IsDirty = false;
            Tabs.Add(tab);
            ActiveTab = tab;
            OnPropertyChanged(nameof(HasTabs));
            if (readOnly)
            {
                MainViewModel.Shell?.Toast(Loc.T("Files_LargeReadOnly"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task CloseTabAsync(EditorTab? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (tab.IsDirty && MainViewModel.Shell is { } shell &&
            !await shell.ConfirmAsync(Loc.T("Files_Unsaved"), tab.Name, Loc.T("Files_Discard"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        var i = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(i, 0, Tabs.Count - 1)];
        OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>Checks YAML / JSON / TOML before saving; returns the error and its 1-based line, or null.</summary>
    public static (string Message, int Line)? CheckSyntax(string extension, string text)
    {
        try
        {
            switch (extension)
            {
                case ".yml" or ".yaml":
                    new YamlDotNet.RepresentationModel.YamlStream().Load(new StringReader(text));
                    break;
                case ".json" or ".mcmeta":
                    using (JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                    {
                    }

                    break;
                case ".toml":
                    var model = Tomlyn.Parsing.SyntaxParser.Parse(text, "file.toml", false);
                    if (model.HasErrors)
                    {
                        var d = model.Diagnostics.First(x => x.Kind == Tomlyn.Syntax.DiagnosticMessageKind.Error);
                        return (d.Message, d.Span.Start.Line + 1);
                    }

                    break;
            }

            return null;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return (ex.Message, (int)ex.Start.Line);
        }
        catch (JsonException ex)
        {
            return (ex.Message, (int)(ex.LineNumber ?? 0) + 1);
        }
    }

    [RelayCommand]
    private async Task SaveAsync(EditorTab? tab)
    {
        tab ??= ActiveTab;
        if (tab is null || tab.ReadOnly)
        {
            return;
        }

        var text = tab.Document.Text;
        if (CheckSyntax(tab.Extension, text) is { } err)
        {
            tab.Error = err.Message;
            tab.ErrorLine = err.Line;
            if (MainViewModel.Shell is { } shell &&
                !await shell.ConfirmAsync(Loc.T("Files_SyntaxError"), Loc.F("Files_SyntaxErrorBody", err.Line, err.Message), Loc.T("Files_SaveAnyway"), danger: true).ConfigureAwait(true))
            {
                return;
            }
        }
        else
        {
            tab.Error = null;
            tab.ErrorLine = 0;
        }

        try
        {
            var full = Confine(tab.FullPath);
            if (File.Exists(full))
            {
                var backupDir = Path.Combine(Root, ".packcheck", "edits", DateTime.Now.ToString("yyyyMMdd", Loc.Instance.Culture));
                Directory.CreateDirectory(backupDir);
                File.Copy(full, Path.Combine(backupDir, $"{DateTime.Now:HHmmss}-{Path.GetFileName(full)}"), overwrite: true);
            }

            await File.WriteAllTextAsync(full, text, new UTF8Encoding(false)).ConfigureAwait(true);
            tab.IsDirty = false;
            MainViewModel.Shell?.Toast(Loc.F("Files_Saved", tab.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = await Ask(Loc.T("Files_NewFolder"), "new-folder").ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Confine(Path.Combine(Current, name)));
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task NewFileAsync()
    {
        var name = await Ask(Loc.T("Files_NewFile"), "new-file.yml").ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        try
        {
            var full = Confine(Path.Combine(Current, name));
            if (!File.Exists(full))
            {
                await File.WriteAllTextAsync(full, string.Empty).ConfigureAwait(true);
            }

            Refresh();
            OpenInEditor(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (Selection() is not [var entry])
        {
            return;
        }

        var name = await Ask(Loc.T("Files_Rename"), entry.Name).ConfigureAwait(true);
        if (name is null || name == entry.Name)
        {
            return;
        }

        try
        {
            if (IsLocked(entry.FullPath))
            {
                throw new IOException(Loc.T("Files_LockedRunning"));
            }

            var target = Confine(Path.Combine(Current, name));
            if (entry.IsDirectory)
            {
                Directory.Move(entry.FullPath, target);
            }
            else
            {
                File.Move(entry.FullPath, target);
            }

            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var items = Selection();
        if (items.Count == 0 || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (items.Any(i => IsLocked(i.FullPath)))
        {
            shell.Toast(Loc.T("Files_LockedRunning"), isError: true);
            return;
        }

        var body = string.Join("\n", items.Take(8).Select(i => "• " + i.Name)) + (items.Count > 8 ? $"\n… +{items.Count - 8}" : string.Empty);
        if (!await shell.ConfirmAsync(Loc.F("Files_DeleteTitle", items.Count), body, Loc.T("Files_Delete"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        foreach (var i in items)
        {
            try
            {
                var full = Confine(i.FullPath);
                if (i.IsDirectory)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Fail(ex);
            }
        }

        Refresh();
    }

    [RelayCommand]
    private void CopySelection() => Remember(cut: false);

    [RelayCommand]
    private void CutSelection() => Remember(cut: true);

    private void Remember(bool cut)
    {
        _clipboard.Clear();
        _clipboard.AddRange(Selection().Select(e => e.FullPath));
        _clipboardIsCut = cut;
        OnPropertyChanged(nameof(HasClipboard));
        MainViewModel.Shell?.Toast(Loc.F(cut ? "Files_Cut" : "Files_Copied", _clipboard.Count));
    }

    [RelayCommand]
    private void Paste()
    {
        foreach (var src in _clipboard)
        {
            try
            {
                var target = Confine(Path.Combine(Current, Path.GetFileName(src)));
                if (target.Equals(src, StringComparison.OrdinalIgnoreCase) || target.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    target = Confine(Path.Combine(Current, Path.GetFileNameWithoutExtension(src) + " - copy" + Path.GetExtension(src)));
                }

                if (_clipboardIsCut && IsLocked(src))
                {
                    throw new IOException(Loc.T("Files_LockedRunning"));
                }

                if (Directory.Exists(src))
                {
                    if (_clipboardIsCut)
                    {
                        Directory.Move(src, target);
                    }
                    else
                    {
                        CopyDirectory(src, target);
                    }
                }
                else if (File.Exists(src))
                {
                    if (_clipboardIsCut)
                    {
                        File.Move(src, target);
                    }
                    else
                    {
                        File.Copy(src, target, overwrite: false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(ex);
            }
        }

        if (_clipboardIsCut)
        {
            _clipboard.Clear();
            OnPropertyChanged(nameof(HasClipboard));
        }

        Refresh();
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    [RelayCommand]
    private async Task ZipSelectionAsync()
    {
        var items = Selection();
        if (items.Count == 0)
        {
            return;
        }

        var zipPath = Confine(Path.Combine(Current, (items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : "archive") + $"-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var i in items)
                {
                    if (i.IsDirectory)
                    {
                        foreach (var f in Directory.EnumerateFiles(i.FullPath, "*", SearchOption.AllDirectories))
                        {
                            zip.CreateEntryFromFile(f, Path.GetRelativePath(Current, f).Replace('\\', '/'), CompressionLevel.Optimal);
                        }
                    }
                    else
                    {
                        zip.CreateEntryFromFile(i.FullPath, i.Name, CompressionLevel.Optimal);
                    }
                }
            }).ConfigureAwait(true);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task UnzipAsync()
    {
        if (Selection() is not [var entry] || !entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var target = Confine(Path.Combine(Current, Path.GetFileNameWithoutExtension(entry.Name)));
            await Task.Run(() => SafeZip.Extract(entry.FullPath, target)).ConfigureAwait(true);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnsafeArchiveException or InvalidDataException)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private void Upload()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Files_Upload") };
        if (dlg.ShowDialog() == true)
        {
            Import(dlg.FileNames);
        }
    }

    /// <summary>Copies files or folders from outside (upload button, drag and drop) into the current folder.</summary>
    public void Import(IEnumerable<string> paths)
    {
        var count = 0;
        foreach (var src in paths)
        {
            try
            {
                var target = Confine(Path.Combine(Current, Path.GetFileName(src)));
                if (IsLocked(target))
                {
                    throw new IOException(Loc.T("Files_LockedRunning"));
                }

                if (Directory.Exists(src))
                {
                    CopyDirectory(src, target);
                }
                else
                {
                    File.Copy(src, target, overwrite: true);
                }

                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(ex);
            }
        }

        Refresh();
        if (count > 0)
        {
            MainViewModel.Shell?.Toast(Loc.F("Files_Uploaded", count));
        }
    }

    [RelayCommand]
    private void Export()
    {
        var items = Selection();
        if (items.Count == 0)
        {
            return;
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Files_Export") };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        foreach (var i in items)
        {
            var dest = Path.Combine(dlg.FolderName, i.Name);
            if (i.IsDirectory)
            {
                CopyDirectory(i.FullPath, dest);
            }
            else
            {
                File.Copy(i.FullPath, dest, overwrite: true);
            }
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        var all = Entries.Any(e => !e.IsSelected);
        foreach (var e in Entries)
        {
            e.IsSelected = all;
        }
    }

    [RelayCommand]
    private void OpenInExplorer() => Shell32.Open(Current);

    private static Task<string?> Ask(string title, string initial) =>
        MainViewModel.Shell?.PromptAsync(title, initial) ?? Task.FromResult<string?>(null);
}
