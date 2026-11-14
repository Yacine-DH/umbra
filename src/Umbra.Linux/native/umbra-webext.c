/*
 * umbra-webext: Umbra's WebKitGTK web-process extension (the first layer of the Linux widget sandbox, issue #180).
 *
 * It sees every request a page makes before it leaves the web process (WebKitWebPage::send-request) and
 *   - rewrites https://umbra.local/X and https://community.umbra.local/X to the umbra:// scheme the host serves
 *     (widgets hard-code https://umbra.local/shared/umbra.js, as on Windows where those are WebView2 virtual hosts);
 *   - lets umbra:, data: and blob: through;
 *   - lets https:// through only to a host the widget declared in widget.json (permissions.hosts), compared exactly and
 *     case-insensitively like WidgetManifest.AllowsHost;
 *   - cancels everything else and tells the host, which logs "[id] sandbox blocked <uri>" as Windows does.
 *
 * The allowed hosts come from the host process, never from the page: when a page object is created the extension asks
 * its view ("umbra.policy?") and the host answers from the widget's manifest. Until the answer is here nothing but
 * Umbra's own origins loads (fail closed). Page scripts cannot send or receive these messages.
 *
 * Build: gcc -shared -fPIC umbra-webext.c $(pkg-config --cflags --libs webkitgtk-web-process-extension-6.0)
 */
#include <string.h>
#include <webkit/webkit-web-process-extension.h>

#define POLICY_KEY "umbra-policy"
#define MSG_POLICY_REQUEST "umbra.policy?"
#define MSG_POLICY "umbra.policy"
#define MSG_BLOCKED "umbra.blocked"
#define MAX_REPORTED_URI 2000

/* UMBRA_WEBEXT_DEBUG=1 in the host's environment traces what the extension decides on stderr (development only). */
static gboolean debug_on;
#define TRACE(...) do { if (debug_on) g_printerr("umbra-webext: " __VA_ARGS__); } while (0)

typedef struct {
    gboolean known;   /* the host answered */
    gboolean open;    /* a host-owned page (Settings): no host list, only the rewrite applies */
    GPtrArray *hosts; /* declared https hosts, lower case */
} Policy;

static void policy_free(gpointer data)
{
    Policy *p = data;
    if (p->hosts) g_ptr_array_unref(p->hosts);
    g_free(p);
}

/* "open" or "sandbox\nhost\nhost...". Anything else leaves the page closed. */
static void policy_apply(Policy *p, const char *text)
{
    if (!text) return;
    char **lines = g_strsplit(text, "\n", -1);
    if (lines[0] && (strcmp(lines[0], "open") == 0 || strcmp(lines[0], "sandbox") == 0)) {
        GPtrArray *hosts = g_ptr_array_new_with_free_func(g_free);
        for (int i = 1; lines[i]; i++)
            if (lines[i][0]) g_ptr_array_add(hosts, g_ascii_strdown(lines[i], -1));
        if (p->hosts) g_ptr_array_unref(p->hosts);
        p->hosts = hosts;
        p->open = strcmp(lines[0], "open") == 0;
        p->known = TRUE;
    }
    g_strfreev(lines);
}

static gboolean is_umbra_host(const char *host)
{
    return host && (g_ascii_strcasecmp(host, "umbra.local") == 0 || g_ascii_strcasecmp(host, "community.umbra.local") == 0);
}

static void report_blocked(WebKitWebPage *page, const char *uri)
{
    char *shown = g_strndup(uri, MAX_REPORTED_URI);
    webkit_web_page_send_message_to_view(page, webkit_user_message_new(MSG_BLOCKED, g_variant_new_string(shown)), NULL, NULL, NULL);
    g_free(shown);
}

/* TRUE cancels the request. */
static gboolean on_send_request(WebKitWebPage *page, WebKitURIRequest *request, WebKitURIResponse *redirected, gpointer user_data)
{
    const char *uri = webkit_uri_request_get_uri(request);
    char *scheme = uri ? g_uri_parse_scheme(uri) : NULL;
    gboolean allow = FALSE;

    if (!scheme) {
        /* no scheme: nothing we can judge, so it does not load */
    } else if (strcmp(scheme, "umbra") == 0 || strcmp(scheme, "data") == 0 || strcmp(scheme, "blob") == 0) {
        allow = TRUE; /* the scheme handler answers 404 for anything outside the two roots */
    } else if (strcmp(scheme, "about") == 0) {
        allow = strcmp(uri, "about:blank") == 0;
    } else if (strcmp(scheme, "https") == 0) {
        GUri *parsed = g_uri_parse(uri, G_URI_FLAGS_ENCODED, NULL);
        if (parsed) {
            const char *host = g_uri_get_host(parsed);
            if (is_umbra_host(host) && g_uri_get_port(parsed) == -1 && !g_uri_get_userinfo(parsed)) {
                char *lower = g_ascii_strdown(host, -1);
                char *local = g_uri_join(G_URI_FLAGS_ENCODED, "umbra", NULL, lower, -1,
                    g_uri_get_path(parsed), g_uri_get_query(parsed), g_uri_get_fragment(parsed));
                webkit_uri_request_set_uri(request, local);
                g_free(local);
                g_free(lower);
                allow = TRUE;
            } else if (host && !is_umbra_host(host)) {
                Policy *p = g_object_get_data(G_OBJECT(page), POLICY_KEY);
                if (p && p->known) {
                    if (p->open) allow = TRUE;
                    else {
                        char *lower = g_ascii_strdown(host, -1);
                        for (guint i = 0; i < p->hosts->len && !allow; i++)
                            allow = strcmp(g_ptr_array_index(p->hosts, i), lower) == 0;
                        g_free(lower);
                    }
                }
            }
            g_uri_unref(parsed);
        }
    }

    g_free(scheme);
    TRACE("%s %s\n", allow ? "allow" : "BLOCK", uri ? uri : "(no uri)");
    if (allow) return FALSE;
    report_blocked(page, uri ? uri : "(no uri)");
    return TRUE;
}

static void on_policy_reply(GObject *source, GAsyncResult *result, gpointer user_data)
{
    WebKitWebPage *page = WEBKIT_WEB_PAGE(source);
    WebKitUserMessage *reply = webkit_web_page_send_message_to_view_finish(page, result, NULL);
    if (!reply) return; /* no answer: the page stays closed */
    Policy *p = g_object_get_data(G_OBJECT(page), POLICY_KEY);
    GVariant *params = webkit_user_message_get_parameters(reply);
    if (p && params && g_variant_is_of_type(params, G_VARIANT_TYPE_STRING))
        policy_apply(p, g_variant_get_string(params, NULL));
    TRACE("policy received: known=%d open=%d hosts=%u\n", p ? p->known : -1, p ? p->open : -1, p && p->hosts ? p->hosts->len : 0);
    g_object_unref(reply);
}

/* The host may push a new policy to a live page (same format as the answer). */
static gboolean on_user_message(WebKitWebPage *page, WebKitUserMessage *message, gpointer user_data)
{
    if (g_strcmp0(webkit_user_message_get_name(message), MSG_POLICY) != 0) return FALSE;
    Policy *p = g_object_get_data(G_OBJECT(page), POLICY_KEY);
    GVariant *params = webkit_user_message_get_parameters(message);
    if (p && params && g_variant_is_of_type(params, G_VARIANT_TYPE_STRING))
        policy_apply(p, g_variant_get_string(params, NULL));
    return TRUE;
}

static void on_page_created(WebKitWebProcessExtension *extension, WebKitWebPage *page, gpointer user_data)
{
    TRACE("page created\n");
    g_object_set_data_full(G_OBJECT(page), POLICY_KEY, g_new0(Policy, 1), policy_free);
    g_signal_connect(page, "send-request", G_CALLBACK(on_send_request), NULL);
    g_signal_connect(page, "user-message-received", G_CALLBACK(on_user_message), NULL);
    webkit_web_page_send_message_to_view(page, webkit_user_message_new(MSG_POLICY_REQUEST, NULL), NULL, on_policy_reply, NULL);
}

G_MODULE_EXPORT void webkit_web_process_extension_initialize(WebKitWebProcessExtension *extension)
{
    debug_on = g_getenv("UMBRA_WEBEXT_DEBUG") != NULL;
    TRACE("loaded\n");
    g_signal_connect(extension, "page-created", G_CALLBACK(on_page_created), NULL);
}
