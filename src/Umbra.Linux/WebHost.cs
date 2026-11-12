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

    static WebKit.NetworkSession CreateSession()
    {
        var root = AppPaths.WebViewDataDir;
        var data = Path.Combine(root, "webkit", "data");
        var cache = Path.Combine(root, "webkit", "cache");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(cache);
        return WebKit.NetworkSession.New(data, cache);
    }

    static WebKit.WebContext CreateContext()
    {
        var context = WebKit.WebContext.New();
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
            Finish(request, File.ReadAllBytes(file), UmbraScheme.ContentType(file), 200, "OK");
        }
        catch (Exception ex)
        {
            Log.Write($"umbra:// request failed: {ex.Message}");
            try { Finish(request, [], "text/plain", 404, "Not Found"); } catch { /* the request is already gone */ }
        }
    }

    static void Finish(WebKit.URISchemeRequest request, byte[] body, string contentType, uint status, string reason)
    {
        Gio.MemoryInputStream stream;
        if (body.Length == 0) stream = Gio.MemoryInputStream.New(); // GLib.Bytes refuses an empty buffer
        else
        {
            using var bytes = GLib.Bytes.New(body);
            stream = Gio.MemoryInputStream.NewFromBytes(bytes);
        }
        var response = WebKit.URISchemeResponse.New(stream, body.Length);
        response.SetContentType(contentType);
        response.SetStatus(status, reason);
        request.FinishWithResponse(response);
    }

    /// <summary>A new web view on the shared context and session, with its own script-message channel.</summary>
    public static WebKit.WebView NewView(out WebKit.UserContentManager content, bool devTools)
    {
        content = WebKit.UserContentManager.New();
        var view = WebKit.WebView.NewWithProperties(
        [
            new GObject.ConstructArgument("web-context", new GObject.Value(Context)),
            new GObject.ConstructArgument("network-session", new GObject.Value(Session)),
            new GObject.ConstructArgument("user-content-manager", new GObject.Value(content)),
        ]);
        var s = view.GetSettings();
        s.EnableDeveloperExtras = devTools;
        s.JavascriptCanOpenWindowsAutomatically = false;
        s.EnableBackForwardNavigationGestures = false;
        s.AllowModalDialogs = false;
        return view;
    }

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
