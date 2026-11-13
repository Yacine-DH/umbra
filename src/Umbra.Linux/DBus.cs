using System.Runtime.InteropServices;
using System.Text;

namespace Umbra.Linux;

/// <summary>
/// A thin layer over GDBus (libgio) for the session-bus services the host offers or uses: the tray (StatusNotifierItem +
/// dbusmenu), notifications, MPRIS, logind and the desktop portal. Values cross as GVariant text (<see cref="V"/>), which
/// keeps every call one readable line. Calls are made directly because the binding registers objects only through GClosures.
/// Everything here runs on the GTK main thread: GDBus dispatches to the main context that was current when an object was
/// registered or a signal subscribed.
/// </summary>
static unsafe partial class DBus
{
    const string Gio = "libgio-2.0.so.0";
    const string GLibLib = "libglib-2.0.so.0";

    [LibraryImport(Gio)] private static partial IntPtr g_bus_get_sync(int busType, IntPtr cancellable, out IntPtr error);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr g_dbus_node_info_new_for_xml(string xml, out IntPtr error);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr g_dbus_node_info_lookup_interface(IntPtr info, string name);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint g_dbus_connection_register_object(IntPtr conn, string path, IntPtr iface, IntPtr vtable, IntPtr userData, IntPtr free, out IntPtr error);
    [LibraryImport(Gio)] private static partial int g_dbus_connection_unregister_object(IntPtr conn, uint id);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int g_dbus_connection_emit_signal(IntPtr conn, string? dest, string path, string iface, string name, IntPtr parameters, out IntPtr error);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr g_dbus_connection_call_sync(IntPtr conn, string dest, string path, string iface, string method, IntPtr parameters,
        IntPtr replyType, int flags, int timeoutMs, IntPtr cancellable, out IntPtr error);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint g_dbus_connection_signal_subscribe(IntPtr conn, string? sender, string? iface, string? member, string? path, string? arg0, int flags,
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> callback, IntPtr userData, IntPtr free);
    [LibraryImport(Gio)] private static partial void g_dbus_connection_signal_unsubscribe(IntPtr conn, uint id);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] private static partial uint g_bus_own_name_on_connection(IntPtr conn, string name, int flags, IntPtr acquired, IntPtr lost, IntPtr data, IntPtr free);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint g_bus_watch_name_on_connection(IntPtr conn, string name, int flags,
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void> appeared, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> vanished, IntPtr data, IntPtr free);
    [LibraryImport(Gio)] private static partial IntPtr g_dbus_connection_get_unique_name(IntPtr conn);
    [LibraryImport(Gio)] private static partial void g_dbus_method_invocation_return_value(IntPtr invocation, IntPtr parameters);
    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] private static partial void g_dbus_method_invocation_return_dbus_error(IntPtr invocation, string name, string message);

    [LibraryImport(GLibLib, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr g_variant_parse(IntPtr type, string text, IntPtr limit, IntPtr endptr, out IntPtr error);
    [LibraryImport(GLibLib)] private static partial void g_variant_unref(IntPtr v);
    [LibraryImport(GLibLib)] private static partial IntPtr g_variant_get_child_value(IntPtr v, nuint index);
    [LibraryImport(GLibLib)] private static partial nuint g_variant_n_children(IntPtr v);
    [LibraryImport(GLibLib)] private static partial IntPtr g_variant_get_string(IntPtr v, out nuint length);
    [LibraryImport(GLibLib)] private static partial IntPtr g_variant_get_type_string(IntPtr v);
    [LibraryImport(GLibLib)] private static partial int g_variant_get_int32(IntPtr v);
    [LibraryImport(GLibLib)] private static partial uint g_variant_get_uint32(IntPtr v);
    [LibraryImport(GLibLib)] private static partial long g_variant_get_int64(IntPtr v);
    [LibraryImport(GLibLib)] private static partial ulong g_variant_get_uint64(IntPtr v);
    [LibraryImport(GLibLib)] private static partial double g_variant_get_double(IntPtr v);
    [LibraryImport(GLibLib)] private static partial int g_variant_get_boolean(IntPtr v);
    [LibraryImport(GLibLib)] private static partial IntPtr g_variant_get_variant(IntPtr v);
    [LibraryImport(GLibLib)] private static partial IntPtr g_variant_print(IntPtr v, int typeAnnotate);
    [LibraryImport(GLibLib)] private static partial void g_free(IntPtr p);
    [LibraryImport(GLibLib)] private static partial void g_error_free(IntPtr error);

    [StructLayout(LayoutKind.Sequential)] struct GError { public uint Domain; public int Code; public IntPtr Message; }

    static string TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero) return "unknown error";
        var m = Marshal.PtrToStringUTF8(((GError*)error)->Message) ?? "unknown error";
        g_error_free(error);
        return m;
    }

    static IntPtr _session;

    /// <summary>The user's session bus, or zero when there is none (a bare TTY, some CI).</summary>
    public static IntPtr Session
    {
        get
        {
            if (_session != IntPtr.Zero) return _session;
            _session = g_bus_get_sync(2 /* G_BUS_TYPE_SESSION */, IntPtr.Zero, out var err);
            if (_session == IntPtr.Zero) Log.Write($"No D-Bus session bus: {TakeError(err)}");
            return _session;
        }
    }

    static IntPtr _system;

    /// <summary>The system bus (logind, UPower), or zero.</summary>
    public static IntPtr SystemBus
    {
        get
        {
            if (_system != IntPtr.Zero) return _system;
            _system = g_bus_get_sync(1 /* G_BUS_TYPE_SYSTEM */, IntPtr.Zero, out var err);
            if (_system == IntPtr.Zero) Log.Write($"No D-Bus system bus: {TakeError(err)}");
            return _system;
        }
    }

    public static string? UniqueName(IntPtr conn) => conn == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(g_dbus_connection_get_unique_name(conn));

    static IntPtr Parse(string text)
    {
        var v = g_variant_parse(IntPtr.Zero, text, IntPtr.Zero, IntPtr.Zero, out var err);
        if (v == IntPtr.Zero) throw new FormatException($"Bad GVariant text ({TakeError(err)}): {(text.Length > 200 ? text[..200] : text)}");
        return v;
    }

    /// <summary>A method call on the bus, blocking up to <paramref name="timeoutMs"/>; returns the reply as a <see cref="Value"/> (dispose it), or throws.</summary>
    public static Value Call(IntPtr conn, string dest, string path, string iface, string method, string? args = null, int timeoutMs = 5000)
    {
        if (conn == IntPtr.Zero) throw new InvalidOperationException("No D-Bus connection");
        var p = args is null ? IntPtr.Zero : Parse(args);
        var r = g_dbus_connection_call_sync(conn, dest, path, iface, method, p, IntPtr.Zero, 0, timeoutMs, IntPtr.Zero, out var err);
        if (p != IntPtr.Zero) g_variant_unref(p);
        if (r == IntPtr.Zero) throw new DBusException(TakeError(err));
        return new Value(r, owned: true);
    }

    /// <summary>Like <see cref="Call"/> on a worker thread (GDBus is thread safe), so a slow service never blocks the UI.</summary>
    public static Task<Value> CallAsync(IntPtr conn, string dest, string path, string iface, string method, string? args = null, int timeoutMs = 5000) =>
        Task.Run(() => Call(conn, dest, path, iface, method, args, timeoutMs));

    public static void Emit(IntPtr conn, string path, string iface, string signal, string? args = null)
    {
        if (conn == IntPtr.Zero) return;
        var p = args is null ? IntPtr.Zero : Parse(args);
        if (g_dbus_connection_emit_signal(conn, null, path, iface, signal, p, out var err) == 0) Log.Verbose($"D-Bus signal {signal} failed: {TakeError(err)}");
        if (p != IntPtr.Zero) g_variant_unref(p);
    }

    public static uint OwnName(IntPtr conn, string name) =>
        conn == IntPtr.Zero ? 0 : g_bus_own_name_on_connection(conn, name, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    // ---------- exported objects ----------

    /// <summary>What an exported object answers: methods (reply text or null for none) and properties (value text).</summary>
    public interface IObject
    {
        /// <summary>Returns the reply as GVariant text (a tuple, e.g. "(true,)"), or null for an empty reply. Throw <see cref="DBusException"/> for an error reply.</summary>
        string? Call(string method, Value args, string sender);
        /// <summary>The property's value as GVariant text, or null when unknown.</summary>
        string? Get(string property);
    }

    static IntPtr _vtable;
    static readonly List<GCHandle> Kept = new();

    /// <summary>Exports <paramref name="handler"/> at <paramref name="path"/> with the interface named in the introspection XML.</summary>
    public static uint Register(IntPtr conn, string path, string xml, string iface, IObject handler)
    {
        if (conn == IntPtr.Zero) return 0;
        if (_vtable == IntPtr.Zero)
        {
            _vtable = Marshal.AllocHGlobal(IntPtr.Size * 11);
            new Span<byte>((void*)_vtable, IntPtr.Size * 11).Clear();
            var slots = (IntPtr*)_vtable;
            slots[0] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnMethod;
            slots[1] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr, IntPtr>)&OnGet;
        }
        var node = g_dbus_node_info_new_for_xml(xml, out var err);
        if (node == IntPtr.Zero) throw new FormatException($"Bad introspection XML: {TakeError(err)}");
        var info = g_dbus_node_info_lookup_interface(node, iface); // the node info is kept for the life of the process
        var handle = GCHandle.Alloc(handler);
        Kept.Add(handle);
        var id = g_dbus_connection_register_object(conn, path, info, _vtable, GCHandle.ToIntPtr(handle), IntPtr.Zero, out err);
        if (id == 0) throw new DBusException($"Could not export {path}: {TakeError(err)}");
        return id;
    }

    public static void Unregister(IntPtr conn, uint id) { if (conn != IntPtr.Zero && id != 0) g_dbus_connection_unregister_object(conn, id); }

    [UnmanagedCallersOnly]
    static void OnMethod(IntPtr conn, IntPtr sender, IntPtr path, IntPtr iface, IntPtr method, IntPtr parameters, IntPtr invocation, IntPtr data)
    {
        var name = Marshal.PtrToStringUTF8(method) ?? "";
        try
        {
            var handler = (IObject)GCHandle.FromIntPtr(data).Target!;
            using var args = new Value(parameters, owned: false);
            var reply = handler.Call(name, args, Marshal.PtrToStringUTF8(sender) ?? "");
            if (reply is null) { g_dbus_method_invocation_return_value(invocation, IntPtr.Zero); return; }
            var v = Parse(reply);
            g_dbus_method_invocation_return_value(invocation, v);
            g_variant_unref(v);
        }
        catch (DBusException ex) { g_dbus_method_invocation_return_dbus_error(invocation, "org.freedesktop.DBus.Error.Failed", ex.Message); }
        catch (Exception ex)
        {
            Log.Write($"D-Bus {name} failed: {ex.Message}");
            g_dbus_method_invocation_return_dbus_error(invocation, "org.freedesktop.DBus.Error.Failed", ex.Message);
        }
    }

    [UnmanagedCallersOnly]
    static IntPtr OnGet(IntPtr conn, IntPtr sender, IntPtr path, IntPtr iface, IntPtr property, IntPtr* error, IntPtr data)
    {
        try
        {
            var handler = (IObject)GCHandle.FromIntPtr(data).Target!;
            var text = handler.Get(Marshal.PtrToStringUTF8(property) ?? "");
            return text is null ? IntPtr.Zero : Parse(text); // GDBus takes the reference
        }
        catch (Exception ex) { Log.Write($"D-Bus property failed: {ex.Message}"); return IntPtr.Zero; }
    }

    // ---------- signals and name watches ----------

    /// <summary>Calls <paramref name="onSignal"/> (on the UI thread) for each matching signal; returns an id for <see cref="Unsubscribe"/>.</summary>
    public static uint Subscribe(IntPtr conn, string? sender, string? iface, string? member, string? path, Action<string, Value> onSignal)
    {
        if (conn == IntPtr.Zero) return 0;
        var handle = GCHandle.Alloc(onSignal);
        Kept.Add(handle);
        return g_dbus_connection_signal_subscribe(conn, sender, iface, member, path, null, 0, &OnSignal, GCHandle.ToIntPtr(handle), IntPtr.Zero);
    }

    public static void Unsubscribe(IntPtr conn, uint id) { if (conn != IntPtr.Zero && id != 0) g_dbus_connection_signal_unsubscribe(conn, id); }

    [UnmanagedCallersOnly]
    static void OnSignal(IntPtr conn, IntPtr sender, IntPtr path, IntPtr iface, IntPtr signal, IntPtr parameters, IntPtr data)
    {
        try
        {
            var cb = (Action<string, Value>)GCHandle.FromIntPtr(data).Target!;
            using var v = new Value(parameters, owned: false);
            cb(Marshal.PtrToStringUTF8(signal) ?? "", v);
        }
        catch (Exception ex) { Log.Write($"D-Bus signal handler failed: {ex.Message}"); }
    }

    /// <summary>Calls <paramref name="changed"/> with true when <paramref name="name"/> appears on the bus (also right away if it is there) and false when it goes.</summary>
    public static uint Watch(IntPtr conn, string name, Action<bool> changed)
    {
        if (conn == IntPtr.Zero) return 0;
        var handle = GCHandle.Alloc(changed);
        Kept.Add(handle);
        return g_bus_watch_name_on_connection(conn, name, 0, &OnAppeared, &OnVanished, GCHandle.ToIntPtr(handle), IntPtr.Zero);
    }

    [UnmanagedCallersOnly]
    static void OnAppeared(IntPtr conn, IntPtr name, IntPtr owner, IntPtr data)
    {
        try { ((Action<bool>)GCHandle.FromIntPtr(data).Target!)(true); } catch (Exception ex) { Log.Write($"D-Bus watch failed: {ex.Message}"); }
    }

    [UnmanagedCallersOnly]
    static void OnVanished(IntPtr conn, IntPtr name, IntPtr data)
    {
        try { ((Action<bool>)GCHandle.FromIntPtr(data).Target!)(false); } catch (Exception ex) { Log.Write($"D-Bus watch failed: {ex.Message}"); }
    }

    // ---------- values ----------

    /// <summary>A read-only view of a GVariant. Children and unboxed variants are new views (dispose them, or let the GC).</summary>
    public sealed class Value : IDisposable
    {
        IntPtr _v;
        readonly bool _owned;
        internal Value(IntPtr v, bool owned) { _v = v; _owned = owned; }
        public string Type => Marshal.PtrToStringUTF8(g_variant_get_type_string(_v)) ?? "";
        public int Count => (int)g_variant_n_children(_v);
        public Value this[int i] => new(g_variant_get_child_value(_v, (nuint)i), owned: true);
        public string Str => Marshal.PtrToStringUTF8(g_variant_get_string(_v, out _)) ?? "";
        public int Int => g_variant_get_int32(_v);
        public uint UInt => g_variant_get_uint32(_v);
        public long Long => g_variant_get_int64(_v);
        public ulong ULong => g_variant_get_uint64(_v);
        public double Double => g_variant_get_double(_v);
        public bool Bool => g_variant_get_boolean(_v) != 0;
        /// <summary>The value inside a "v".</summary>
        public Value Unbox => new(g_variant_get_variant(_v), owned: true);

        /// <summary>Any number as a long ("i", "u", "x", "t", "n", "q", "y", "d"); 0 for other types.</summary>
        public long Number => Type switch
        {
            "i" => Int, "u" => UInt, "x" => Long, "t" => (long)ULong, "d" => (long)Double,
            "n" or "q" or "y" => long.TryParse(Text, out var n) ? n : 0,
            "v" => Unbox.Number,
            _ => 0,
        };

        /// <summary>A string-ish value ("s", "o", "g"), or the value unboxed from a "v"; null otherwise.</summary>
        public string? AsString => Type switch { "s" or "o" or "g" => Str, "v" => Unbox.AsString, _ => null };

        /// <summary>An a{sv} as a dictionary of boxed values.</summary>
        public Dictionary<string, Value> Dict()
        {
            var d = new Dictionary<string, Value>();
            if (!Type.StartsWith("a{s", StringComparison.Ordinal)) return d;
            for (var i = 0; i < Count; i++)
            {
                using var e = this[i];
                var v = e[1];
                d[e[0].Str] = v.Type == "v" ? v.Unbox : v;
            }
            return d;
        }

        /// <summary>The GVariant text form (for logs and tests).</summary>
        public string Text
        {
            get
            {
                var p = g_variant_print(_v, 0);
                var s = Marshal.PtrToStringUTF8(p) ?? "";
                g_free(p);
                return s;
            }
        }

        public void Dispose()
        {
            if (_owned && _v != IntPtr.Zero) g_variant_unref(_v);
            _v = IntPtr.Zero;
        }
    }
}

/// <summary>An error reply from (or for) a D-Bus call.</summary>
sealed class DBusException(string message) : Exception(message);

/// <summary>Builds GVariant text: quoting is the only place untrusted text meets the format, so it is all here (and unit tested).</summary>
static class V
{
    /// <summary>A string literal: single quotes, with backslash, quote and every control or non-ASCII character escaped.</summary>
    public static string Str(string? s)
    {
        var sb = new StringBuilder("'");
        foreach (var c in s ?? "")
        {
            if (c == '\\' || c == '\'') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7e) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    public static string Bool(bool b) => b ? "true" : "false";
    public static string Int(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string UInt(uint u) => "uint32 " + u.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string Long(long l) => "int64 " + l.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string Path(string p) => "objectpath " + Str(p);
    public static string Box(string value) => "<" + value + ">";

    /// <summary>An a{sv} from already-formatted values (each is boxed here); empty gives a typed empty dictionary.</summary>
    public static string Dict(IEnumerable<(string Key, string Value)> entries)
    {
        var list = entries.Select(e => $"{Str(e.Key)}: <{e.Value}>").ToList();
        return list.Count == 0 ? "@a{sv} {}" : "{" + string.Join(", ", list) + "}";
    }

    public static string Strings(IEnumerable<string> items)
    {
        var list = items.Select(Str).ToList();
        return list.Count == 0 ? "@as []" : "[" + string.Join(", ", list) + "]";
    }
}
