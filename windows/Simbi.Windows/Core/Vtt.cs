using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Simbi.Windows.Core;

public sealed record Cue(string Id, double Start, double End, string Speaker, string Text)
{
    public string Time => Vtt.Timestamp(Start);
    public string Display => $"{Time}   {Speaker}\n{Text}";
}

public static class Vtt
{
    private static readonly object Gate = new();
    public static string Timestamp(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    public static IReadOnlyList<Cue> Read(string folder)
    {
        var path = Path.Combine(folder, "transcript.vtt");
        if (!File.Exists(path)) return [];
        return Parse(File.ReadAllText(path));
    }
    public static IReadOnlyList<Cue> Parse(string text)
    {
        var result = new List<Cue>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            var lines = block.Split('\n');
            var index = Array.FindIndex(lines, l => l.Contains(" --> "));
            if (index < 0) continue;
            var times = lines[index].Split(" --> ");
            if (!TryTime(times[0], out var start) || !TryTime(times[1].Split(' ')[0], out var end)) continue;
            var payload = string.Join("\n", lines.Skip(index + 1));
            var voice = Regex.Match(payload, @"<v(?:\.[^ >]+)?\s+([^>]+)>");
            var speaker = voice.Success ? WebUtility.HtmlDecode(voice.Groups[1].Value) : "Speaker";
            var plain = WebUtility.HtmlDecode(Regex.Replace(payload, "<[^>]*>", ""));
            result.Add(new(index > 0 ? lines[index - 1].Trim() : $"legacy-{result.Count}", start, end, speaker, plain));
        }
        return result.OrderBy(c => c.Start).ToList();
    }
    private static bool TryTime(string value, out double seconds)
    {
        var fields = value.Split(':');
        seconds = 0;
        if (fields.Length is < 2 or > 3) return false;
        foreach (var field in fields)
        {
            if (!double.TryParse(field, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var part)) return false;
            seconds = seconds * 60 + part;
        }
        return double.IsFinite(seconds) && seconds >= 0;
    }
    // A blank line ends a WebVTT cue, so cue text must never contain one.
    public static string CueText(string text) => Regex.Replace(text.Trim(), @"\r?\n\s*\n", "\n").Replace("\r\n", "\n");
    public static void Append(string folder, Cue cue)
    {
        lock (Gate)
        {
            if (Read(folder).Any(c => c.Id == cue.Id)) return;
            var path = Path.Combine(folder, "transcript.vtt");
            var text = File.Exists(path) ? File.ReadAllText(path).TrimEnd() : "WEBVTT";
            var speaker = WebUtility.HtmlEncode(cue.Speaker).Replace("\n", " ");
            AtomicFile.Write(path, $"{text}\n\n{cue.Id}\n{Timestamp(cue.Start)} --> {Timestamp(cue.End)}\n<v {speaker}>{WebUtility.HtmlEncode(CueText(cue.Text))}</v>\n\n");
        }
    }
}
