using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Simbi.Windows.Core;

public sealed record Note(string Folder, string Title)
{
    public override string ToString() => Title;
}

public sealed class NoteStore
{
    public string Root { get; }
    public NoteStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public IReadOnlyList<Note> List() => Directory.EnumerateFiles(Root, "note.md", new EnumerationOptions
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        MaxRecursionDepth = 12
    }).Select(p => new Note(Path.GetDirectoryName(p)!, Path.GetRelativePath(Root, Path.GetDirectoryName(p)!)))
        .OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Note Create(string title)
    {
        var safe = Regex.Replace(title.Trim(), "[<>:\"/\\\\|?*\\x00-\\x1F]", "-").TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "Untitled";
        if (safe.Length > 100) safe = safe[..100].TrimEnd('.', ' ');
        if (Regex.IsMatch(safe, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) safe = "_" + safe;
        var folder = Path.Combine(Root, safe);
        for (var i = 2; Directory.Exists(folder); i++) folder = Path.Combine(Root, $"{safe} ({i})");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        Directory.CreateDirectory(Path.Combine(folder, "context"));
        AtomicFile.Write(Path.Combine(folder, "note.md"), "# " + title.Trim() + "\n\n");
        return new Note(folder, Path.GetFileName(folder));
    }

    public string ReadText(Note note, string file) => File.Exists(Path.Combine(note.Folder, file))
        ? File.ReadAllText(Path.Combine(note.Folder, file)) : "";

    public void SaveText(Note note, string file, string text, string? expected = null)
    {
        var target = Path.Combine(note.Folder, file);
        if (expected != null && ReadText(note, file) != expected)
        {
            var recovery = Path.Combine(note.Folder, ".simbi", "conflicts", $"{DateTime.UtcNow:yyyyMMdd-HHmmss-ffff}-{file}");
            AtomicFile.Write(recovery, text);
            throw new NoteConflictException(file, recovery, $"{file} changed in another app. Your edits were preserved at {recovery}; the external version has been reloaded.");
        }
        if (expected != null && File.Exists(target)) AtomicFile.WriteChecked(target, text, expected);
        else AtomicFile.Write(target, text);
    }

    public string Import(Note note, string source)
    {
        var folder = Path.Combine(note.Folder, "files");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(source));
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(source)} ({n}){Path.GetExtension(source)}");
        File.Copy(source, target, false);
        if (new[] { ".md", ".txt", ".csv", ".json", ".vtt" }.Contains(Path.GetExtension(source).ToLowerInvariant()))
            AtomicFile.Write(Path.Combine(note.Folder, "context", Path.GetFileName(target) + ".md"), File.ReadAllText(target));
        return target;
    }
}

public static class AtomicFile
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void WriteJson<T>(string path, T value) => Write(path, JsonSerializer.Serialize(value, Json));
    public static void WriteChecked(string path, string content, string expected)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = Path.Combine(Path.GetDirectoryName(path)!, ".simbi", "conflicts", $"{Guid.NewGuid():N}-external-{Path.GetFileName(path)}");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        try
        {
            Write(temp, content);
            // Windows atomically backs up the exact replaced version, covering the compare/replace race.
            File.Replace(temp, path, backup);
            if (File.ReadAllText(backup) != expected)
                throw new NoteConflictException(Path.GetFileName(path), backup, $"{Path.GetFileName(path)} changed during save. The external version is preserved at {backup}; your version is in the document.");
            File.Delete(backup);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class AppSettings
{
    public string NotesRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Simbi");
    public string CodexPath { get; set; } = "";
    public bool Microphone { get; set; } = true;
    public bool SystemAudio { get; set; } = true;
    public bool Transcribe { get; set; } = false;
    public string MicrophoneId { get; set; } = "";
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Simbi", "settings.json");
    public static AppSettings Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), AtomicFile.Json) ?? new() : new();
    public void Save() => AtomicFile.WriteJson(FilePath, this);
}
