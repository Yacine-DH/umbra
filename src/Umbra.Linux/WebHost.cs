using System.Diagnostics;

namespace Umbra;

/// <summary>The shared WebKit context and network session, the <c>umbra://</c> scheme handler and common hardening for every Umbra web view.</summary>
static class WebHost
{
    static WebKit.WebContext? _context;
    static WebKit.NetworkSession? _session;

    /// <summary>One context for all views: it owns the <c>umbra</c> scheme.</summary>
    public static WebKit.WebContext Context => _context ??= CreateContext();

    /// <summary>One persistent session: website data and cache live under the XDG cache folder (or inside a throwaway/portable profile).</summary>
    public static WebKit.NetworkSession Session => _session ??= CreateSession();

    static WebKit.NetworkSession CreateSession() => NewSession("host");

    static WebKit.NetworkSession NewSession(string name)
    {
        var root = Path.Combine(AppPaths.WebViewDataDir, "webkit", name);
        var data = Path.Combine(root, "data");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(cache);
        return WebKit.NetworkSession.New(data, cache);
    }

    static readonly Dictionary<string, WebKit.NetworkSession> WidgetSessions = new();
    static bool _proxyNoteLogged;

    /// <summary>
    /// The network session of one widget (all its copies share it), the third layer of the sandbox: its only route to the
    /// network is a proxy address where nothing listens, except for the widget's declared hosts, which connect directly.
    /// So a connection the engine opens on its own before any policy runs (the preconnect for a navigation, a prefetch, a
    /// DNS lookup for a proxied name) never leaves this machine. It also gives each widget its own cookies and web storage.
    /// </summary>
    static WebKit.NetworkSession SessionFor(WidgetManifest widget)
    {
        if (!WidgetSessions.TryGetValue(widget.Id, out var session))
            WidgetSessions[widget.Id] = session = NewSession(Path.Combine("widgets", widget.Id));
        // Applied on every new view: an update may have changed the declared hosts.
        if (Sandbox.DeadEndProxy(Prefs.Proxy, Environment.GetEnvironmentVariable) is { } deadEnd)
            session.SetProxySettings(WebKit.NetworkProxyMode.Custom,
                WebKit.NetworkProxySettings.New(deadEnd, Sandbox.CleanHosts(widget.Permissions.Hosts).ToArray()));
        else if (!_proxyNoteLogged)
        {
            _proxyNoteLogged = true;
            Log.Write("A proxy is configured, so widget pages use it for every host: the request filter and the page policy still block undeclared hosts.");
        }
        return session;
    }

    /// <summary>The installed widgets, for the per-widget Content-Security-Policy. Set by <c>App</c>.</summary>
    public static Func<IReadOnlyList<WidgetManifest>> Manifests { get; set; } = () => [];

    /// <summary>The folder with <c>libumbra-webext.so</c>, the web-process extension that enforces the sandbox (next to the binary).</summary>
    public static string ExtensionDir => Path.Combine(AppPaths.AppDir, "webext");

    /// <summary>False when the sandbox extension is missing: widget pages are then not opened at all (see <c>App</c>).</summary>
    public static bool SandboxAvailable => File.Exists(Path.Combine(ExtensionDir, "libumbra-webext.so"));

    static WebKit.WebContext CreateContext()
    {
        var context = WebKit.WebContext.New();
        // The web process runs in WebKit's own bubblewrap sandbox, which must be told it may read the extension's folder.
        context.AddPathToSandbox(ExtensionDir, true);
        // Every web process loads the sandbox extension before it creates a page.
        context.OnInitializeWebProcessExtensions += (c, _) => c.SetWebProcessExtensionsDirectory(ExtensionDir);
        context.RegisterUriScheme(UmbraScheme.Scheme, Serve);
        // Secure: pages on it are a secure context (crypto.subtle, no mixed-content blocks for https hosts).
        // CORS-enabled: the two origins behave like the two https virtual hosts on Windows.
        var security = context.GetSecurityManager();
        security.RegisterUriSchemeAsSecure(UmbraScheme.Scheme);
        security.RegisterUriSchemeAsCorsEnabled(UmbraScheme.Scheme);
        return context;
    }

    /// <summary>Answers one <c>umbra://</c> request from disk; anything outside the two roots is a 404.</summary>
    static void Serve(WebKit.URISchemeRequest request)
    {
        try
        {
            var uri = request.GetUri();
            var method = request.GetHttpMethod();
            var file = method is "GET" or "HEAD" ? UmbraScheme.Resolve(uri, AppPaths.AppDir, AppPaths.CommunityDir) : null;
            if (file is null)
            {
                Log.Verbose($"umbra:// 404 {uri}");
                Finish(request, [], "text/plain", 404, "Not Found");
                return;
            }
            Finish(request, File.ReadAllBytes(file), UmbraScheme.ContentType(file), 200, "OK", Sandbox.CspFor(uri, Manifests()));
        }
        catch (Exception ex)
        {
            Log.Write($"umbra:// request failed: {ex.Message}");
            try { Finish(request, [], "text/plain", 404, "Not Found"); } catch { /* the request is already gone */ }
        }
    }

    static void Finish(WebKit.URISchemeRequest request, byte[] body, string contentType, uint status, string reason, string? csp = null)
    {
        Gio.MemoryInputStream stream;
        if (body.Length == 0) stream = Gio.MemoryInputStream.New(); // GLib.Bytes refuses an empty buffer
        else
        {
            using var bytes = GLib.Bytes.New(body);
            stream = Gio.MemoryInputStream.NewFromBytes(bytes);
        }
        var response = WebKit.URISchemeResponse.New(stream, body.Length);
        response.SetStatus(status, reason);
        var headers = Soup.MessageHeaders.New(Soup.MessageHeadersType.Response);
        headers.Append("Content-Type", contentType); // custom headers replace the type WebKit would add, and nosniff needs it
        if (csp is not null) headers.Append("Content-Security-Policy", csp);
        headers.Append("X-Content-Type-Options", "nosniff");
        headers.Append("Cache-Control", "no-store"); // files change on disk (widget updates, dev loop); always read them again
        response.SetHttpHeaders(headers);
        response.SetContentType(contentType);
        request.FinishWithResponse(response);
    }

    /// <summary>
    /// A new web view on the shared context and session, with its own script-message channel, locked down like
    /// <c>WebHost.InitAsync</c> on Windows.
    /// </summary>
    /// <param name="sandbox">The widget whose page this is (its declared hosts are the only reachable ones), or null for a host-owned page.</param>
    /// <param name="label">Who the view belongs to, for the log.</param>
    public static WebKit.WebView NewView(out WebKit.UserContentManager content, bool devTools, WidgetManifest? sandbox, string label)
    {
        content = WebKit.UserContentManager.New();
        var view = WebKit.WebView.NewWithProperties(
        [
            new GObject.ConstructArgument("web-context", new GObject.Value(Context)),
            new GObject.ConstructArgument("network-session", new GObject.Value(sandbox is null ? Session : SessionFor(sandbox))),
            new GObject.ConstructArgument("user-content-manager", new GObject.Value(content)),
        ]);
        var s = view.GetSettings();
        s.EnableDeveloperExtras = devTools;
        s.JavascriptCanOpenWindowsAutomatically = false;
        s.EnableBackForwardNavigationGestures = false;
        s.AllowModalDialogs = false;
        // Dev hook: UMBRA_WEB_CONSOLE=1 prints the pages' console (CSP reports included) on the host's stdout.
        s.EnableWriteConsoleMessagesToStdout = Environment.GetEnvironmentVariable("UMBRA_WEB_CONSOLE") == "1";
        s.EnableDnsPrefetching = false; // a prefetch would leak host names past the sandbox
        s.EnableWebrtc = false;
        s.EnableMediaStream = false;
        Lock(view, sandbox, label);
        return view;
    }

    /// <summary>The sandbox policy channel to the web-process extension, the navigation lock and "no new windows".</summary>
    static void Lock(WebKit.WebView view, WidgetManifest? sandbox, string label)
    {
        long blockedLogged = 0;
        var windowStart = Environment.TickCount64;

        // A physical mouse press on this view, seen by GTK before the page gets it: the one thing a page cannot fake.
        // Only a navigation right after it may open the user's browser (WebKit's own "user gesture" flag is not enough).
        var pressedAt = long.MinValue / 2;
        var press = Gtk.GestureClick.New();
        press.SetButton(0);
        press.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        press.OnPressed += (_, _) => pressedAt = Environment.TickCount64;
        view.AddController(press);
        bool PressedJustNow() => Environment.TickCount64 - pressedAt <= 2000;

        // Only the extension can send these (page scripts cannot); a lying web process could at most read its own policy.
        view.OnUserMessageReceived += (_, e) =>
        {
            switch (e.Message.GetName())
            {
                case "umbra.policy?":
                    e.Message.SendReply(WebKit.UserMessage.New("umbra.policy", GLib.Variant.NewString(Sandbox.PolicyText(sandbox))));
                    return true;
                case "umbra.blocked":
                    // At most 30 lines a minute per view, so a looping page cannot flood the log.
                    var now = Environment.TickCount64;
                    if (now - windowStart > 60_000) { windowStart = now; blockedLogged = 0; }
                    if (blockedLogged++ < 30 && e.Message.GetParameters() is { } p)
                        Log.Write($"[{label}] sandbox blocked {p.GetString(out nuint _)}");
                    return true;
                default:
                    return false;
            }
        };

        // Top-level pages may only be Umbra's own; a clicked link opens in the user's browser instead.
        view.OnDecidePolicy += (_, e) =>
        {
            if (e.DecisionType is not (WebKit.PolicyDecisionType.NavigationAction or WebKit.PolicyDecisionType.NewWindowAction)) return false;
            if (e.Decision is not WebKit.NavigationPolicyDecision decision) return false;
            var action = decision.GetNavigationAction();
            var url = action.GetRequest().GetUri();
            var verdict = Sandbox.Navigation(url, action.IsUserGesture() && PressedJustNow());
            if (verdict == NavigationVerdict.Allow && e.DecisionType == WebKit.PolicyDecisionType.NewWindowAction)
                verdict = NavigationVerdict.Block; // no second window, even for our own pages
            if (verdict == NavigationVerdict.Allow) return false;
            decision.Ignore();
            if (verdict == NavigationVerdict.External) OpenExternal(url);
            else Log.Write($"[{label}] sandbox blocked navigation to {Short(url)}");
            return true;
        };
        // window.open never creates a view. A link the user really clicked still reaches their browser.
        view.OnCreate += (_, e) =>
        {
            var url = e.NavigationAction.GetRequest().GetUri();
            if (Sandbox.Navigation(url, e.NavigationAction.IsUserGesture() && PressedJustNow()) == NavigationVerdict.External) OpenExternal(url);
            else Log.Write($"[{label}] sandbox blocked new window for {Short(url)}");
            return null!;
        };
    }

    static string Short(string? url) => url is null ? "(none)" : url.Length <= 300 ? url : url[..300] + "...";


    /// <summary>Opens an http(s) link in the user's browser; anything else is ignored.</summary>
    public static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return;
        try
        {
            // Fixed argv, no shell: the URL is a single argument.
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(u.AbsoluteUri);
            using var p = Process.Start(psi);
        }
        catch (Exception ex) { Log.Write($"Open URL failed: {ex.Message}"); }
    }
}
