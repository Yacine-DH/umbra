using System.Text.RegularExpressions;

namespace Umbra.Cli;

/// <summary>umbractl new: writes a minimal, working widget that follows the SDK and design guidelines.</summary>
static partial class Scaffold
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex Id();

    public const string SchemaUrl = "https://raw.githubusercontent.com/AmineChr54/umbra/main/schemas/widget.schema.json";

    public static IReadOnlyDictionary<string, string> Files(string id, string name) => new Dictionary<string, string>
    {
        ["widget.json"] = $$"""
            {
              "$schema": "{{SchemaUrl}}",
              "id": "{{id}}",
              "name": "{{name}}",
              "description": "Describe in one sentence why people want this widget.",
              "author": "your-github-name",
              "version": "0.1.0",
              "manifestVersion": 1,
              "category": "productivity",
              "entry": "index.html",
              "width": 260,
              "height": 120,
              "permissions": { "hosts": [] },
              "options": {
                "greeting": { "label": "Greeting", "type": "text", "default": "Hello" }
              }
            }

            """,
        ["index.html"] = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <link rel="stylesheet" href="https://umbra.local/shared/umbra.css">
              <link rel="stylesheet" href="style.css">
            </head>
            <body>
              <main class="card">
                <div class="label" id="greeting"></div>
                <div class="value num" id="time"></div>
              </main>
              <script src="https://umbra.local/shared/umbra.js"></script>
              <script src="main.js"></script>
            </body>
            </html>

            """,
        ["style.css"] = """
            /* True black for OLED; tokens like --text, --text-2 and --accent come from umbra.css. */
            html, body { margin: 0; height: 100%; background: #000; color: var(--text); overflow: hidden; }
            .card { height: 100%; box-sizing: border-box; padding: 18px 20px; display: grid; align-content: center; gap: 4px; }
            .label { color: var(--text-2); font-size: 13px; }
            .value { font-size: 34px; font-weight: 300; }

            """,
        ["main.js"] = """
            // Runs now and again on the user's refresh interval. umbra.options holds this widget's settings.
            function render() {
              document.getElementById("greeting").textContent = umbra.options.greeting;
              document.getElementById("time").textContent =
                new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
              umbra.rendered(); // first frame is drawn (umbractl capture waits for it)
            }
            umbra.every(render);
            umbra.onSettings(render);

            """,
    };

    /// <summary>Creates parentDir/id with the template files. Returns the folder.</summary>
    public static string Create(string id, string name, string parentDir)
    {
        if (!Id().IsMatch(id)) throw new ArgumentException($"Invalid widget id '{id}': use 2-40 lowercase letters, digits and dashes");
        var dir = Path.Combine(parentDir, id);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new IOException($"{dir} already exists and is not empty");
        Directory.CreateDirectory(dir);
        foreach (var (file, content) in Files(id, name))
            File.WriteAllText(Path.Combine(dir, file), content.ReplaceLineEndings("\n"));
        return dir;
    }
}
