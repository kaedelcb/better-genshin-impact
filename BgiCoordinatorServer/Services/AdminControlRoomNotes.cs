using System.Text.Json;

namespace BgiCoordinatorServer.Services;

internal sealed class AdminControlRoomNotes(string logRoot)
{
    internal const int MaxLength = 120;
    private readonly string _path = Path.Combine(logRoot, "assistant-room-notes.json");
    private readonly object _lock = new();

    public IReadOnlyDictionary<string, string> Read()
    {
        lock (_lock) return ReadFile();
    }

    public bool TrySet(string code, string note, bool knownRoom)
    {
        lock (_lock)
        {
            var notes = ReadFile();
            if (!knownRoom && !notes.ContainsKey(code)) return false;
            if (note.Length == 0)
            {
                if (!notes.Remove(code)) return true;
            }
            else
            {
                if (notes.TryGetValue(code, out var old) && old == note) return true;
                notes[code] = note;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, notes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
            return true;
        }
    }

    private Dictionary<string, string> ReadFile()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            var notes = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (notes is null || notes.Any(pair => !IsRoomCode(pair.Key) || !IsValidNote(pair.Value) ||
                    string.IsNullOrWhiteSpace(pair.Value)))
                throw new JsonException("Invalid assistant room notes file.");
            return notes;
        }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new(StringComparer.Ordinal); }
    }

    internal static bool IsRoomCode(string code) => code.Length == 6 && code.All(char.IsAsciiLetterOrDigit);
    internal static bool IsValidNote(string? note) => note is not null && note.Length <= MaxLength && !note.Any(char.IsControl);
}
