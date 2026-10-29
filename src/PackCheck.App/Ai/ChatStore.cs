using System.IO;
using System.Text.Json;

namespace PackCheck.App.Ai;

/// <summary>One saved line: user, assistant and status lines; <paramref name="Files"/> are attachment file names stored next to the chat.</summary>
public sealed record SavedChatLine(string Role, string Text, IReadOnlyList<string>? Files = null);

/// <summary>A whole conversation as stored on disk.</summary>
public sealed record SavedChat(string Id, string Title, string? ServerId, DateTimeOffset Updated, List<SavedChatLine> Lines);

/// <summary>What the history list shows for a conversation.</summary>
public sealed record ChatSummary(string Id, string Title, string? ServerId, DateTimeOffset Updated, int Count);

/// <summary>
/// Conversations under <c>root/chats/{id}.json</c>, their attachments under <c>root/attachments/{id}/</c>.
/// Older versions kept one file per server (<c>root/{serverId}.json</c>); <see cref="MigrateLegacy"/> turns those into chats.
/// </summary>
public sealed class ChatStore(string root)
{
    public const int MaxLines = 400;

    private string ChatsDir => Path.Combine(root, "chats");

    public string AttachmentDir(string chatId) => Path.Combine(root, "attachments", chatId);

    public static string NewId() => DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The first user message, on one line and shortened, names the chat.</summary>
    public static string MakeTitle(IEnumerable<SavedChatLine> lines)
    {
        var first = lines.FirstOrDefault(l => l.Role == "user" && !string.IsNullOrWhiteSpace(l.Text))?.Text ?? string.Empty;
        var line = string.Join(' ', first.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 70 ? line[..67].TrimEnd() + "…" : line;
    }

    public IReadOnlyList<ChatSummary> List()
    {
        if (!Directory.Exists(ChatsDir))
        {
            return [];
        }

        var list = new List<ChatSummary>();
        foreach (var file in Directory.EnumerateFiles(ChatsDir, "*.json"))
        {
            if (Read(file) is { } c)
            {
                list.Add(new ChatSummary(c.Id, c.Title, c.ServerId, c.Updated, c.Lines.Count(l => l.Role is "user" or "assistant")));
            }
        }

        return list.OrderByDescending(c => c.Updated).ToList();
    }

    public SavedChat? Load(string id) => IsSafeId(id) ? Read(Path.Combine(ChatsDir, id + ".json")) : null;

    public void Save(SavedChat chat)
    {
        if (!IsSafeId(chat.Id))
        {
            return;
        }

        Directory.CreateDirectory(ChatsDir);
        var trimmed = chat with { Lines = chat.Lines.TakeLast(MaxLines).ToList() };
        var file = Path.Combine(ChatsDir, chat.Id + ".json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(trimmed));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public void Delete(string id)
    {
        if (!IsSafeId(id))
        {
            return;
        }

        File.Delete(Path.Combine(ChatsDir, id + ".json"));
        var dir = AttachmentDir(id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Copies an attachment next to the chat and returns the stored file name (unique within the chat).</summary>
    public string SaveAttachment(string chatId, string name, byte[] data)
    {
        var dir = AttachmentDir(chatId);
        Directory.CreateDirectory(dir);
        var safe = string.Concat(Path.GetFileName(name).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (safe.Length == 0)
        {
            safe = "file";
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var target = safe;
        for (var i = 2; File.Exists(Path.Combine(dir, target)); i++)
        {
            target = $"{stem} ({i}){ext}";
        }

        File.WriteAllBytes(Path.Combine(dir, target), data);
        return target;
    }

    /// <summary>Turns the old per-server history files into chats; the old file is renamed to *.migrated so nothing is lost.</summary>
    public int MigrateLegacy()
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                var lines = JsonSerializer.Deserialize<List<SavedChatLine>>(File.ReadAllText(file));
                if (lines is { Count: > 0 })
                {
                    var key = Path.GetFileNameWithoutExtension(file);
                    var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                    Save(new SavedChat(NewId(), MakeTitle(lines), key == "general" ? null : key, updated, lines));
                    count++;
                }

                File.Move(file, file + ".migrated", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return count;
    }

    private static bool IsSafeId(string id) => id.Length is > 0 and < 80 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static SavedChat? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<SavedChat>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
