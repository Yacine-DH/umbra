extern alias cli;

using System.Text.Json.Nodes;
using CommandLine = cli::Umbra.Cli.CommandLine;
using McpServer = cli::Umbra.Cli.McpServer;
using Scaffold = cli::Umbra.Cli.Scaffold;

namespace Umbra.Tests;

public class UpdateAllCommandTests
{
    static Task<JsonObject> NoSend(string c, JsonObject a) => Task.FromResult(Protocol.Ok(null));

    [Theory]
    [InlineData("update", "--all")]
    [InlineData("update-all")]
    public async Task Cli_maps_update_all(params string[] args) =>
        Assert.Equal("update-all", (await CommandLine.TranslateAsync(args, NoSend)).Cmd);

    [Fact]
    public async Task Plain_update_still_only_checks_for_an_umbra_update() =>
        Assert.Equal("check-update", (await CommandLine.TranslateAsync(new[] { "update" }, NoSend)).Cmd);

    [Fact]
    public void Protocol_and_mcp_expose_update_all()
    {
        Assert.Contains("update-all", Protocol.Commands.Select(c => c.Cmd));
        Assert.Contains("update_all_widgets", McpServer.ToolNames);
    }
}

public class McpServerTests
{
    readonly List<(string Cmd, JsonObject Args)> _sent = new();
    JsonObject _reply = Protocol.Ok(new JsonArray());

    McpServer Server() => new((cmd, args) => { _sent.Add((cmd, args)); return Task.FromResult(_reply); });

    static JsonObject Call(string method, JsonObject? @params = null, int id = 1) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params ?? new JsonObject() };

    [Fact]
    public async Task Initialize_negotiates_a_supported_version()
    {
        var known = await Server().HandleAsync(Call("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }));
        Assert.Equal("2025-03-26", known!["result"]!["protocolVersion"]!.GetValue<string>());
        var unknown = await Server().HandleAsync(Call("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }));
        Assert.Equal("2025-06-18", unknown!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(unknown["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task Install_widget_never_forwards_acceptPermissions()
    {
        // Accepting new widget access is the user's decision, so an agent can't pass it through the MCP tool.
        await Server().HandleAsync(Call("tools/call", new JsonObject
        {
            ["name"] = "install_widget",
            ["arguments"] = new JsonObject { ["id"] = "todo", ["acceptPermissions"] = true },
        }));
        var (cmd, args) = Assert.Single(_sent);
        Assert.Equal("install", cmd);
        Assert.Equal("todo", args["id"]!.GetValue<string>());
        Assert.Null(args["acceptPermissions"]);
    }

    [Fact]
    public async Task Install_url_maps_the_flags_and_has_no_mcp_tool()
    {
        var empty = (string _, JsonObject __) => Task.FromResult(new JsonObject());
        var (cmd, a) = await CommandLine.TranslateAsync(new[] { "install-url", "https://github.com/x/w.umbrawidget" }, empty);
        Assert.Equal("install-url", cmd);
        Assert.Equal("https://github.com/x/w.umbrawidget", a!["url"]!.GetValue<string>());
        Assert.Null(a["acceptPermissions"]);
        Assert.Null(a["overwrite"]);
        var (_, b) = await CommandLine.TranslateAsync(new[] { "install-url", "--overwrite", "https://github.com/x/w.umbrawidget", "--accept-permissions" }, empty);
        Assert.True(b!["overwrite"]!.GetValue<bool>());
        Assert.True(b["acceptPermissions"]!.GetValue<bool>());
        Assert.DoesNotContain("install-url", McpServer.ToolCommands);
        Assert.DoesNotContain(McpServer.ToolNames, n => n.Contains("url", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<ArgumentException>(() => CommandLine.TranslateAsync(new[] { "install-url" }, empty));
    }

    [Fact]
    public async Task Pack_widget_over_mcp_cannot_choose_the_output_or_overwrite()
    {
        await Server().HandleAsync(Call("tools/call", new JsonObject
        {
            ["name"] = "pack_widget",
            ["arguments"] = new JsonObject { ["path"] = @"C:\w\clock", ["out"] = @"C:\Windows\x.umbrawidget", ["overwrite"] = true },
        }));
        var (cmd, args) = Assert.Single(_sent);
        Assert.Equal("pack-widget", cmd);
        Assert.Equal(@"C:\w\clock", args["path"]!.GetValue<string>());
        Assert.Null(args["out"]);
        Assert.Null(args["overwrite"]);
        Assert.DoesNotContain("install-local", McpServer.ToolCommands);
        Assert.DoesNotContain("install-url", McpServer.ToolCommands);
    }

    [Fact]
    public async Task Notifications_get_no_reply()
    {
        var note = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
        Assert.Null(await Server().HandleAsync(note));
    }

    [Fact]
    public async Task Tools_list_has_schemas_for_every_tool()
    {
        var tools = (await Server().HandleAsync(Call("tools/list")))!["result"]!["tools"]!.AsArray();
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "create_widget");
        Assert.All(tools, t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));
        Assert.Equal(McpServer.ToolNames.Count(), tools.Count);
    }

    [Fact]
    public async Task Tools_call_maps_to_the_control_command()
    {
        _reply = Protocol.Ok(new JsonObject { ["id"] = "sp500" });
        var res = await Server().HandleAsync(Call("tools/call", new JsonObject
        {
            ["name"] = "configure_widget",
            ["arguments"] = new JsonObject { ["id"] = "sp500", ["options"] = new JsonObject { ["symbol"] = "^IXIC" } },
        }));
        Assert.Equal("configure", _sent.Single().Cmd);
        Assert.Equal("^IXIC", _sent.Single().Args["options"]!["symbol"]!.GetValue<string>());
        Assert.Equal("text", res!["result"]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Null(res["result"]!["isError"]);
    }

    [Fact]
    public async Task Configure_widget_accepts_a_scale_and_edit_mode_is_a_tool()
    {
        var tools = (await Server().HandleAsync(Call("tools/list")))!["result"]!["tools"]!.AsArray();
        var configure = tools.Single(t => t!["name"]!.GetValue<string>() == "configure_widget")!;
        Assert.Equal("number", configure["inputSchema"]!["properties"]!["scale"]!["type"]!.GetValue<string>());
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "edit_mode");
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "lock_all");
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "unlock_all");

        await Server().HandleAsync(Call("tools/call", new JsonObject { ["name"] = "configure_widget", ["arguments"] = new JsonObject { ["id"] = "sp500", ["scale"] = 1.5 } }));
        await Server().HandleAsync(Call("tools/call", new JsonObject { ["name"] = "edit_mode", ["arguments"] = new JsonObject { ["on"] = true } }));
        Assert.Equal(("configure", 1.5), (_sent[0].Cmd, _sent[0].Args["scale"]!.GetValue<double>()));
        Assert.Equal(("edit-mode", true), (_sent[1].Cmd, _sent[1].Args["on"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Widget_data_passes_any_json_value_through()
    {
        await Server().HandleAsync(Call("tools/call", new JsonObject
        {
            ["name"] = "widget_data",
            ["arguments"] = new JsonObject { ["id"] = "todo", ["op"] = "set", ["key"] = "tasks", ["value"] = new JsonArray(1, 2) },
        }));
        var (cmd, args) = _sent.Single();
        Assert.Equal("data", cmd);
        Assert.Equal("[1,2]", args["value"]!.ToJsonString());
    }

    [Fact]
    public async Task Capture_returns_an_image_block()
    {
        _reply = Protocol.Ok(new JsonObject { ["base64"] = "iVBORw0=", ["mimeType"] = "image/png" });
        var res = await Server().HandleAsync(Call("tools/call", new JsonObject { ["name"] = "capture", ["arguments"] = new JsonObject { ["target"] = "sp500" } }));
        var block = res!["result"]!["content"]![0]!;
        Assert.Equal("image", block["type"]!.GetValue<string>());
        Assert.Equal("iVBORw0=", block["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Host_errors_become_tool_errors()
    {
        _reply = Protocol.Error("No widget 'x'");
        var res = await Server().HandleAsync(Call("tools/call", new JsonObject { ["name"] = "remove_widget", ["arguments"] = new JsonObject { ["id"] = "x" } }));
        Assert.True(res!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("No widget", res["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_method_and_tool_are_rpc_errors()
    {
        Assert.Equal(-32601, (await Server().HandleAsync(Call("nope")))!["error"]!["code"]!.GetValue<int>());
        var res = await Server().HandleAsync(Call("tools/call", new JsonObject { ["name"] = "nope" }));
        Assert.Equal(-32602, res!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_speaks_newline_delimited_json()
    {
        var input = new StringReader("""{"jsonrpc":"2.0","id":7,"method":"ping"}""" + "\n{oops\n");
        var output = new StringWriter();
        await Server().RunAsync(input, output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, JsonNode.Parse(lines[0])!["id"]!.GetValue<int>());
        Assert.Equal(-32700, JsonNode.Parse(lines[1])!["error"]!["code"]!.GetValue<int>());
    }
}

public class McpResourcesAndPromptsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "umbra-mcp-" + Guid.NewGuid().ToString("N"));

    public McpResourcesAndPromptsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "demo", "sub"));
        File.WriteAllText(Path.Combine(_dir, "demo", "widget.json"), "{\"id\":\"demo\"}");
        File.WriteAllText(Path.Combine(_dir, "demo", "sub", "a.js"), "// a");
        File.WriteAllBytes(Path.Combine(_dir, "demo", "p.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(_dir, "demo", "secret.exe"), "x");
        File.WriteAllText(Path.Combine(_dir, "outside.json"), "{}");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    McpServer Server() => new((cmd, args) => Task.FromResult(Protocol.Ok(new JsonArray(
        new JsonObject { ["id"] = "demo", ["folder"] = Path.Combine(_dir, "demo") }))));

    static JsonObject Call(string method, JsonObject? @params = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = @params ?? new JsonObject() };

    [Fact]
    public async Task Initialize_advertises_resources_and_prompts()
    {
        var caps = (await Server().HandleAsync(Call("initialize")))!["result"]!["capabilities"]!;
        Assert.NotNull(caps["resources"]);
        Assert.NotNull(caps["prompts"]);
    }

    [Fact]
    public async Task Resources_list_has_allowed_files_only()
    {
        var uris = (await Server().HandleAsync(Call("resources/list")))!["result"]!["resources"]!.AsArray()
            .Select(r => r!["uri"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "umbra://widgets/demo/p.png", "umbra://widgets/demo/sub/a.js", "umbra://widgets/demo/widget.json" }, uris);
    }

    [Fact]
    public async Task Resources_read_returns_text_and_base64_blobs()
    {
        var text = (await Server().HandleAsync(Call("resources/read", new JsonObject { ["uri"] = "umbra://widgets/demo/widget.json" })))!["result"]!["contents"]![0]!;
        Assert.Equal("{\"id\":\"demo\"}", text["text"]!.GetValue<string>());
        Assert.Equal("application/json", text["mimeType"]!.GetValue<string>());
        var blob = (await Server().HandleAsync(Call("resources/read", new JsonObject { ["uri"] = "umbra://widgets/demo/p.png" })))!["result"]!["contents"]![0]!;
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), blob["blob"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("umbra://widgets/demo/../outside.json", -32602)]
    [InlineData("umbra://widgets/demo/..%2Foutside.json", -32002)]
    [InlineData("umbra://widgets/demo/secret.exe", -32602)]
    [InlineData("umbra://widgets/demo/sub\\a.js", -32602)]
    [InlineData("umbra://widgets/demo//widget.json", -32602)]
    [InlineData("umbra://widgets/other/widget.json", -32002)]
    [InlineData("umbra://widgets/demo/missing.json", -32002)]
    [InlineData("file:///c:/windows/win.ini", -32602)]
    public async Task Resources_read_rejects_bad_uris(string uri, int code)
    {
        var reply = (await Server().HandleAsync(Call("resources/read", new JsonObject { ["uri"] = uri })))!;
        Assert.Equal(code, reply["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("umbra://widgets/demo/nul.json")]
    [InlineData("umbra://widgets/demo/CON.txt")]
    [InlineData("umbra://widgets/demo/sub/com1.js")]
    [InlineData("umbra://widgets/demo/lpt9.tar.json")]
    public async Task Resources_read_rejects_reserved_device_names(string uri)
    {
        var reply = (await Server().HandleAsync(Call("resources/read", new JsonObject { ["uri"] = uri })))!;
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Resources_skip_junctions()
    {
        var outside = Path.Combine(_dir, "outside-dir");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "leak.json"), "{}");
        var link = Path.Combine(_dir, "demo", "link");
        if (OperatingSystem.IsWindows())
        {
            // A junction: unlike a directory symlink it needs no admin rights or developer mode.
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            p.WaitForExit();
        }
        else Directory.CreateSymbolicLink(link, outside);
        Assert.True(Directory.Exists(link));

        var reply = (await Server().HandleAsync(Call("resources/read", new JsonObject { ["uri"] = "umbra://widgets/demo/link/leak.json" })))!;
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        var uris = (await Server().HandleAsync(Call("resources/list")))!["result"]!["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>());
        Assert.DoesNotContain(uris, u => u.Contains("leak.json"));
        Directory.Delete(link); // remove the junction itself, not its target
    }

    [Fact]
    public async Task Non_string_params_give_json_rpc_errors()
    {
        var read = (await Server().HandleAsync(Call("resources/read", new JsonObject { ["uri"] = 42 })))!;
        Assert.Equal(-32602, read["error"]!["code"]!.GetValue<int>());
        var prompt = (await Server().HandleAsync(Call("prompts/get", new JsonObject { ["name"] = new JsonObject() })))!;
        Assert.Equal(-32602, prompt["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Prompts_carry_the_safety_note()
    {
        foreach (var (name, args) in new[] { ("new_widget", "description"), ("fix_widget", "id"), ("restyle_widget", "id") })
        {
            var a = new JsonObject { [args] = "x", ["style"] = "y" };
            var text = (await Server().HandleAsync(Call("prompts/get", new JsonObject { ["name"] = name, ["arguments"] = a })))!["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>();
            Assert.Contains("permissions.commands", text);
        }
    }

    [Fact]
    public async Task Prompts_list_and_get()
    {
        var prompts = (await Server().HandleAsync(Call("prompts/list")))!["result"]!["prompts"]!.AsArray();
        Assert.Equal(McpServer.PromptNames.Order(), prompts.Select(p => p!["name"]!.GetValue<string>()).Order());

        var got = (await Server().HandleAsync(Call("prompts/get", new JsonObject
        {
            ["name"] = "new_widget", ["arguments"] = new JsonObject { ["description"] = "a clock" },
        })))!["result"]!;
        var text = got["messages"]![0]!["content"]!["text"]!.GetValue<string>();
        Assert.Contains("a clock", text);
        Assert.Contains("widget_authoring_guide", text);
        Assert.Contains("create_widget", text);
        Assert.Contains("capture", text);
    }

    [Fact]
    public async Task Prompts_get_validates_name_and_required_arguments()
    {
        var unknown = (await Server().HandleAsync(Call("prompts/get", new JsonObject { ["name"] = "nope" })))!;
        Assert.Equal(-32602, unknown["error"]!["code"]!.GetValue<int>());
        var missing = (await Server().HandleAsync(Call("prompts/get", new JsonObject { ["name"] = "fix_widget" })))!;
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
    }
}

public class CommandLineTests
{
    static readonly JsonObject List = Protocol.Ok(new JsonArray(new JsonObject
    {
        ["id"] = "clock",
        ["optionSchema"] = new JsonObject { ["seconds"] = new JsonObject { ["type"] = "toggle" }, ["size"] = new JsonObject { ["type"] = "number" } },
    }));

    static async Task<(string Cmd, string Args)> T(params string[] args)
    {
        var (cmd, a) = await CommandLine.TranslateAsync(args, (_, _) => Task.FromResult(List));
        return (cmd, a.ToJsonString());
    }

    [Fact]
    public async Task Install_takes_first_non_flag_as_id()
    {
        Assert.Equal(("install", """{"id":"todo","acceptPermissions":true}"""), await T("install", "--accept-permissions", "todo"));
        Assert.Equal(("install", """{"id":"todo","acceptPermissions":true}"""), await T("install", "todo", "--accept-permissions"));
    }

    [Theory]
    [InlineData("1.5", """{"id":"sp500","scale":1.5}""")]
    [InlineData("150%", """{"id":"sp500","scale":1.5}""")]
    [InlineData("0.5", """{"id":"sp500","scale":0.5}""")]
    public async Task Configure_scale(string scale, string expected) =>
        Assert.Equal(("configure", expected), await T("configure", "sp500", "--scale", scale));

    [Fact]
    public async Task Configure_takes_several_flags() =>
        Assert.Equal(("configure", """{"id":"sp500","scale":2,"enabled":true,"x":-100,"y":40}"""),
            await T("configure", "sp500", "--scale", "2", "--enabled", "on", "--x", "-100", "--y", "40"));

    [Fact]
    public async Task Install_accepts_new_permissions_only_with_the_flag()
    {
        Assert.Equal(("install", """{"id":"todo"}"""), await T("install", "todo"));
        Assert.Equal(("install", """{"id":"todo","acceptPermissions":true}"""), await T("install", "todo", "--accept-permissions"));
    }

    [Fact]
    public async Task Configure_rejects_a_bad_scale() =>
        await Assert.ThrowsAsync<ArgumentException>(() => T("configure", "sp500", "--scale", "big"));

    [Fact]
    public async Task Placement_commands_map_to_the_protocol()
    {
        Assert.Equal(("configure", """{"id":"sp500","monitor":"2"}"""), await T("configure", "sp500", "--monitor", "2"));
        Assert.Equal(("peek", "{}"), await T("peek"));
        Assert.Equal(("peek", """{"seconds":8}"""), await T("peek", "8"));
        Assert.Equal(("set-general", """{"hideOnFullscreen":true,"peekSeconds":3}"""), await T("general", "--hide-fullscreen", "on", "--peek-seconds", "3"));
        Assert.Equal(("set-general", """{"snapGap":16}"""), await T("general", "--snap-gap", "16"));
        Assert.Equal(("set-general", """{"snap":false}"""), await T("general", "--snap", "off"));
        Assert.Equal(("set-general", """{"snapMode":"always"}"""), await T("general", "--snap-mode", "always"));
        Assert.Equal(("set-general", """{"slowOnEnergySaver":false}"""), await T("general", "--energy-saver", "off"));
    }

    [Fact]
    public async Task Edit_mode_on_off_or_query()
    {
        Assert.Equal(("edit-mode", """{"on":true}"""), await T("edit-mode", "on"));
        Assert.Equal(("edit-mode", """{"on":false}"""), await T("edit-mode", "off"));
        Assert.Equal(("edit-mode", "{}"), await T("edit-mode"));
        await Assert.ThrowsAsync<ArgumentException>(() => T("edit-mode", "maybe"));
    }

    [Fact]
    public async Task Existing_shortcuts_still_map_to_configure()
    {
        Assert.Equal(("lock-all", "{}"), await T("lock-all"));
        Assert.Equal(("unlock-all", "{}"), await T("unlock-all"));
        Assert.Equal(("configure", """{"id":"sp500","x":10,"y":20}"""), await T("move", "sp500", "10", "20"));
        Assert.Equal(("configure", """{"id":"clock","options":{"seconds":true}}"""), await T("set", "clock", "seconds", "on"));
        Assert.Equal(("configure", """{"id":"clock","options":{"size":2.5}}"""), await T("set", "clock", "size", "2.5"));
    }

    [Fact]
    public async Task Unknown_command_is_an_error() =>
        await Assert.ThrowsAsync<ArgumentException>(() => T("frobnicate"));
}

public class ScaffoldTests
{
    [Fact]
    public void Template_is_a_valid_widget()
    {
        var files = Scaffold.Files("hello-world", "Hello");
        var m = WidgetManifest.Parse(files["widget.json"]);
        Assert.Equal("hello-world", m.Id);
        Assert.True(files.ContainsKey(m.Entry));
        Assert.All(files.Keys, f => Assert.True(Community.IsSafeRelativePath(f), f));
        Assert.All(m.Options, kv => Control.CheckOption(m, kv.Key, kv.Value!["default"]));
    }

    [Fact]
    public void Create_writes_the_folder_and_refuses_to_clobber()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"umbra-scaffold-{Guid.NewGuid():N}");
        var dir = Scaffold.Create("my-widget", "My", parent);
        Assert.True(File.Exists(Path.Combine(dir, "widget.json")));
        Assert.Throws<IOException>(() => Scaffold.Create("my-widget", "My", parent));
        Assert.Throws<ArgumentException>(() => Scaffold.Create("Bad", "x", parent));
    }

    // Commands reachable only through "umbractl send" or without an MCP tool, on purpose.
    static readonly string[] NoMcpTool = { "quit", "install-local", "install-url" };

    [Fact]
    public void Install_local_is_never_an_mcp_tool()
    {
        // An agent must not install a local widget (or accept its access); only the person at the keyboard does.
        Assert.DoesNotContain("install-local", McpServer.ToolCommands);
        Assert.DoesNotContain(McpServer.ToolNames, n => n.Contains("local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Install_local_maps_to_a_full_path_and_the_accept_flag()
    {
        var (cmd, a) = await CommandLine.TranslateAsync(new[] { "install-local", "my-widget.zip", "--accept-permissions" }, (_, _) => Task.FromResult(new System.Text.Json.Nodes.JsonObject()));
        Assert.Equal("install-local", cmd);
        Assert.Equal(Path.GetFullPath("my-widget.zip"), a!["path"]!.GetValue<string>());
        Assert.True(a["acceptPermissions"]!.GetValue<bool>());
        var (_, b) = await CommandLine.TranslateAsync(new[] { "install-local", @"C:\w\clock" }, (_, _) => Task.FromResult(new System.Text.Json.Nodes.JsonObject()));
        Assert.Null(b!["acceptPermissions"]);
    }
    static readonly string[] NoVerb = { "set-general", "create-widget", "open-settings" };

    [Fact]
    public void Every_protocol_command_has_an_mcp_tool()
    {
        var tools = McpServer.ToolCommands.ToHashSet();
        var missing = Protocol.Commands.Select(c => c.Cmd).Where(c => !tools.Contains(c) && !NoMcpTool.Contains(c)).ToList();
        Assert.True(missing.Count == 0, "No MCP tool for: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task Every_protocol_command_has_a_command_line_mapping()
    {
        var unmapped = new List<string>();
        foreach (var (cmd, _) in Protocol.Commands.Where(c => !NoVerb.Contains(c.Cmd)))
        {
            try { await CommandLine.TranslateAsync(new[] { cmd }, (_, _) => Task.FromResult(new System.Text.Json.Nodes.JsonObject())); }
            catch (ArgumentException ex) when (ex.Message.StartsWith("Unknown command")) { unmapped.Add(cmd); }
            catch (Exception) { /* a missing argument still proves the verb exists */ }
        }
        Assert.True(unmapped.Count == 0, "No command line mapping for: " + string.Join(", ", unmapped));
    }
}

public class UmbraClientLocateTests
{
    [LinuxFact]
    public void Linux_finds_the_app_from_the_tarball_layout_and_the_install_folder()
    {
        var c = cli::Umbra.Cli.UmbraClient.Candidates(false, null, "/home/u/.local/opt/umbra/umbractl/", "", "/home/u").ToList();
        Assert.Equal("/home/u/.local/opt/umbra/umbractl/umbra", c[0]);
        Assert.Contains("/home/u/.local/opt/umbra/umbra/umbra", c);
        Assert.DoesNotContain(c, p => p.Contains("/.local/lib/"));
    }

    [Fact]
    public void Umbra_exe_wins_and_windows_keeps_the_installer_folder()
    {
        Assert.Equal("/x/umbra", cli::Umbra.Cli.UmbraClient.Candidates(false, "/x/umbra", "/b/", "", "/h").First());
        var w = cli::Umbra.Cli.UmbraClient.Candidates(true, null, @"C:\b", @"C:\L", @"C:\H").ToList();
        Assert.Equal(Path.Combine(@"C:\L", "Programs", "Umbra", "Umbra.exe"), w[^1]);
        Assert.Equal(2, w.Count);
    }
}
