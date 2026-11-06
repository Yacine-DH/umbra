using System.Runtime.InteropServices;
using System.Text;

namespace Umbra;

/// <summary>
/// Reads a Windows shortcut (.lnk) the user picked in Settings > Add script: where it points, its arguments and its
/// "Start in" folder. Only reads: the script that results is shown in the editor and saved (approved) like a typed one.
/// </summary>
static class ShellLink
{
    public sealed record Link(string Target, string Arguments, string WorkingDirectory);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink;

    const uint SLGP_RAWPATH = 0x4, STGM_READ = 0;

    /// <summary>The shortcut's target, arguments and Start in, with environment variables expanded; null if it can't be read or points nowhere on disk.</summary>
    public static Link? Read(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new CShellLink();
            ((IPersistFile)obj).Load(lnkPath, STGM_READ);
            var link = (IShellLinkW)obj;
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var args = new StringBuilder(4096);
            link.GetArguments(args, args.Capacity);
            var dir = new StringBuilder(1024);
            link.GetWorkingDirectory(dir, dir.Capacity);
            var t = Environment.ExpandEnvironmentVariables(target.ToString()).Trim();
            if (t.Length == 0) return null; // a shortcut to a non-file item (Control Panel, a store app)
            return new Link(t, args.ToString().Trim(), Environment.ExpandEnvironmentVariables(dir.ToString()).Trim());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read shortcut {lnkPath}: {ex.Message}");
            return null;
        }
        finally
        {
            if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
    }

    /// <summary>
    /// How the editor shows a shortcut: a .ps1/.py/.sh target becomes that kind of script file; "powershell -File x.ps1"
    /// becomes the PowerShell script x.ps1 with the arguments after it; anything else runs as a program.
    /// </summary>
    public static (string Kind, string Path, string Args) AsScript(Link link)
    {
        var ext = System.IO.Path.GetExtension(link.Target).ToLowerInvariant();
        switch (ext)
        {
            case ".ps1": return ("powershell", link.Target, link.Arguments);
            case ".py" or ".pyw": return ("python", link.Target, link.Arguments);
            case ".sh": return ("bash", link.Target, link.Arguments);
        }
        var exe = System.IO.Path.GetFileNameWithoutExtension(link.Target);
        if (exe.Equals("powershell", StringComparison.OrdinalIgnoreCase) || exe.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var a = UserScript.SplitArgs(link.Arguments);
                var i = a.FindIndex(x => x.Equals("-File", StringComparison.OrdinalIgnoreCase) || x.Equals("-f", StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i + 1 < a.Count && a[i + 1].EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                    return ("powershell", a[i + 1], JoinArgs(a.Skip(i + 2)));
            }
            catch (InvalidOperationException) { } // an unclosed quote: keep it as a program line
        }
        return ("program", link.Target, link.Arguments);
    }

    /// <summary>The reverse of <see cref="UserScript.SplitArgs"/>: quotes an argument with spaces, escapes quotes.</summary>
    public static string JoinArgs(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length == 0 || a.Any(c => c is ' ' or '\t' or '"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
}
