using System.Text.Json;
using Simbi.Windows.Core;

internal static class FakeAppServer
{
    public static async Task RunAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var json = JsonDocument.Parse(line); var message = json.RootElement;
            if (!message.TryGetProperty("id", out var id)) continue;
            var method = message.GetProperty("method").GetString();
            object result = method switch
            {
                "thread/start" => new { thread = new { id = "synthetic-thread" } },
                _ => new { }
            };
            if (method == "turn/start")
            {
                var parameters = message.GetProperty("params");
                var prompt = parameters.GetProperty("input")[0].GetProperty("text").GetString()!;
                var text = "# Synthetic AI notes\n\nDecision: ship Windows ✓";
                if (prompt.Contains("Question:\n")) text = "Question received: " + prompt.Split("Question:\n")[1];
                if (prompt.StartsWith("Read the attachment at ")) text = "# Converted attachment\n\n" + prompt.Split(". Return a faithful")[0];
                // Deliberately notify completion before responding to turn/start.
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { method = "item/completed", @params = new { threadId = "synthetic-thread", item = new { type = "agentMessage", text } } }));
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { method = "turn/completed", @params = new { threadId = "synthetic-thread", turn = new { status = "completed" } } }));
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.Clone(), result }));
            await Console.Out.FlushAsync();
        }
    }
    public static async Task VerifyClientAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await CodexClient.ConnectAsync(Environment.ProcessPath!, root, timeout.Token);
        var answer = await client.AskAsync(root, "Synthetic protocol test", timeout.Token);
        if (!answer.Contains("Windows ✓")) throw new Exception("Codex early completion race or Unicode response failed.");
        Console.WriteLine("PASS: Codex thread/turn lifecycle, early completion race and Unicode AI response (synthetic local server)");
    }
}
