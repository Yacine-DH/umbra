using System.Runtime.InteropServices;

namespace Umbra;

/// <summary>
/// PNG of one web view's visible content, through <c>webkit_web_view_get_snapshot</c> (the Linux <c>CapturePreviewAsync</c>).
/// It asks WebKit for that view's own rendering, with its transparent background kept: never the screen, never another window.
/// The binding has no method for the async call, so it is made directly.
/// </summary>
static unsafe partial class Snapshot
{
    const string WebKitLib = "libwebkitgtk-6.0.so.4";
    const string GtkLib = "libgtk-4.so.1";
    const string GObjectLib = "libgobject-2.0.so.0";
    const string GLibLib = "libglib-2.0.so.0";

    const int RegionVisible = 0;          // WEBKIT_SNAPSHOT_REGION_VISIBLE
    const int TransparentBackground = 2;  // WEBKIT_SNAPSHOT_OPTIONS_TRANSPARENT_BACKGROUND

    [LibraryImport(WebKitLib)]
    private static partial void webkit_web_view_get_snapshot(IntPtr view, int region, int options, IntPtr cancellable,
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback, IntPtr userData);

    [LibraryImport(WebKitLib)]
    private static partial IntPtr webkit_web_view_get_snapshot_finish(IntPtr view, IntPtr result, out IntPtr error);

    [LibraryImport(GtkLib)]
    private static partial IntPtr gdk_texture_save_to_png_bytes(IntPtr texture);

    [LibraryImport(GObjectLib)]
    private static partial void g_object_unref(IntPtr obj);

    [LibraryImport(GLibLib)]
    private static partial IntPtr g_bytes_get_data(IntPtr bytes, out nuint size);

    [LibraryImport(GLibLib)]
    private static partial void g_bytes_unref(IntPtr bytes);

    [LibraryImport(GLibLib)]
    private static partial void g_error_free(IntPtr error);

    [StructLayout(LayoutKind.Sequential)]
    struct GError { public uint Domain; public int Code; public IntPtr Message; }

    /// <summary>Takes the snapshot. Call on the UI thread; the task completes there too.</summary>
    public static Task<byte[]> PngAsync(WebKit.WebView view)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = GCHandle.Alloc(tcs);
        webkit_web_view_get_snapshot(view.Handle.DangerousGetHandle(), RegionVisible, TransparentBackground, IntPtr.Zero,
            &OnDone, GCHandle.ToIntPtr(state));
        return tcs.Task;
    }

    [UnmanagedCallersOnly]
    static void OnDone(IntPtr view, IntPtr result, IntPtr userData)
    {
        var state = GCHandle.FromIntPtr(userData);
        var tcs = (TaskCompletionSource<byte[]>)state.Target!;
        state.Free();
        try
        {
            var texture = webkit_web_view_get_snapshot_finish(view, result, out var error);
            if (texture == IntPtr.Zero)
            {
                var message = "the web view gave no picture";
                if (error != IntPtr.Zero)
                {
                    message = Marshal.PtrToStringUTF8(((GError*)error)->Message) ?? message;
                    g_error_free(error);
                }
                tcs.TrySetException(new InvalidOperationException($"Capture failed: {message}"));
                return;
            }
            var bytes = gdk_texture_save_to_png_bytes(texture);
            g_object_unref(texture);
            if (bytes == IntPtr.Zero) { tcs.TrySetException(new InvalidOperationException("Capture failed: the picture could not be encoded")); return; }
            var data = g_bytes_get_data(bytes, out var size);
            var png = new byte[(int)size];
            if (size > 0) Marshal.Copy(data, png, 0, png.Length);
            g_bytes_unref(bytes);
            tcs.TrySetResult(png);
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }
}
