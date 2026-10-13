using System.Text.Json;

namespace Simbi.Windows.Core;

public sealed record RecordingStamp(bool Active, double Seconds, long AudioLength, long ModifiedTicks, string HeaderHash)
{
    public static string PathFor(string folder) => Path.Combine(folder, ".simbi", "windows-state.json");
    public static void Save(string folder, bool active, double seconds)
    {
        var audio = Path.Combine(folder, "audio.webm"); var file = new FileInfo(audio);
        AtomicFile.WriteJson(PathFor(folder), new RecordingStamp(active, seconds, file.Length, file.LastWriteTimeUtc.Ticks, OpusWebM.HeaderFingerprint(audio)));
    }
    // True when the last session was still marked active, i.e. the app exited mid-recording.
    public static bool WasInterrupted(string folder)
    {
        try
        {
            var path = PathFor(folder); if (!File.Exists(path)) return false;
            return JsonSerializer.Deserialize<RecordingStamp>(File.ReadAllText(path), AtomicFile.Json)?.Active == true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
    public static bool Matches(string folder)
    {
        try
        {
            var path = PathFor(folder); if (!File.Exists(path)) return false;
            var stamp = JsonSerializer.Deserialize<RecordingStamp>(File.ReadAllText(path), AtomicFile.Json);
            var audio = Path.Combine(folder, "audio.webm"); var file = new FileInfo(audio);
            return stamp != null && stamp.HeaderHash == OpusWebM.HeaderFingerprint(audio) &&
                (stamp.Active || stamp.AudioLength == file.Length && stamp.ModifiedTicks == file.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return false; }
    }
}
