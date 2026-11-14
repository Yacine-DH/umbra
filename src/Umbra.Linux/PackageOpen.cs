using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Umbra;

/// <summary>
/// Opening a .umbrawidget from the file manager (#365): which command-line argument is the package (the .desktop entry passes
/// it as <c>%f</c>), whether a path may be reviewed at all, and a private copy of the file for <see cref="Community.PrepareLocal"/>.
/// The copy is read once through a descriptor opened without following a final symlink and without blocking (a FIFO or a
/// device never hangs the review), and capped, so what is reviewed is what gets installed even if the file changes later.
/// </summary>
static partial class PackageOpen
{
    /// <summary>The unpacked widget may be 5 MB (<see cref="Community"/>); the file gets room for zip headers on top.</summary>
    public const long MaxFileBytes = 6 * 1024 * 1024;

    /// <summary>
    /// The full path of the first argument that names a .umbrawidget (a plain path, relative to <paramref name="cwd"/>, or a
    /// <c>file://</c> URI), or null. Flags (<c>-</c>...) are skipped.
    /// </summary>
    public static string? FromArgs(IEnumerable<string> args, string cwd)
    {
        foreach (var raw in args)
        {
            if (string.IsNullOrEmpty(raw) || raw.StartsWith('-')) continue;
            var a = raw;
            if (a.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || !uri.IsFile || !string.IsNullOrEmpty(uri.Host)) continue;
                a = uri.LocalPath;
            }
            else if (a.Contains("://", StringComparison.Ordinal)) continue; // only local files, never a URL
            if (!a.EndsWith(Community.PackageExtension, StringComparison.OrdinalIgnoreCase)) continue;
            try { return Path.GetFullPath(a, cwd); }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// Why <paramref name="path"/> can't be opened as a package, or null: an absolute, normalized path (no <c>.</c> or <c>..</c>
    /// segments) ending in .umbrawidget, with no control characters (the open-package pipe line is one line).
    /// </summary>
    public static string? CheckPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "No file given";
        if (path.Any(char.IsControl)) return "The file name has control characters";
        if (!Path.IsPathRooted(path) || path[0] != '/') return "Not an absolute path";
        if (Path.GetFullPath(path) != path || path.EndsWith('/')) return "Not a normalized path";
        if (!path.EndsWith(Community.PackageExtension, StringComparison.OrdinalIgnoreCase)) return $"Not a {Community.PackageExtension} file";
        return null;
    }

    /// <summary>
    /// Copies the package into a new private folder under the temp folder and returns the copy's path (delete its folder when done).
    /// Refuses a symlink as the last path component, anything that is not a regular file and anything over <see cref="MaxFileBytes"/>.
    /// </summary>
    public static string CopyForReview(string path)
    {
        if (CheckPath(path) is { } bad) throw new InvalidOperationException(bad);
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) throw new InvalidOperationException("The file is a symbolic link; open the file it points to");
        using var handle = OpenNoFollow(path);
        if (handle.IsInvalid)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(errno == ELOOP ? "The file is a symbolic link; open the file it points to" : $"Could not open the file (error {errno})");
        }
        if (!IsRegularFile(handle)) throw new InvalidOperationException("Not a regular file");
        if (RandomAccess.GetLength(handle) > MaxFileBytes) throw new InvalidOperationException("The file is larger than 5 MB");

        var dir = Path.Combine(Path.GetTempPath(), $"umbra-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var copy = Path.Combine(dir, "package" + Community.PackageExtension);
            using (var src = new FileStream(handle, FileAccess.Read))
            using (var dst = new FileStream(copy, FileMode.CreateNew, FileAccess.Write))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > MaxFileBytes) throw new InvalidOperationException("The file is larger than 5 MB");
                    dst.Write(buf, 0, n);
                }
            }
            return copy;
        }
        catch
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>Deletes the private folder <see cref="CopyForReview"/> made.</summary>
    public static void DeleteCopy(string? copy)
    {
        if (copy is null || Path.GetDirectoryName(copy) is not { } dir || !Path.GetFileName(dir).StartsWith("umbra-open-", StringComparison.Ordinal)) return;
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    const int ELOOP = 40;
    const int O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    const uint S_IFMT = 0xF000, S_IFREG = 0x8000;

    /// <summary>O_NOFOLLOW differs by architecture (asm-generic 0x20000 on x86-64, 0x8000 on arm/arm64).</summary>
    static int ONoFollow => RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ? 0x8000 : 0x20000;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    static SafeFileHandle OpenNoFollow(string path) => new(open(path, O_NONBLOCK | O_CLOEXEC | ONoFollow /* O_RDONLY = 0 */), ownsHandle: true);

    /// <summary>The descriptor's file type from <c>statx</c> (glibc 2.28+), whose <c>stx_mode</c> sits at the same offset on every architecture, unlike <c>struct stat</c>.</summary>
    static bool IsRegularFile(SafeFileHandle handle)
    {
        var buf = new byte[256]; // struct statx
        if (statx((int)handle.DangerousGetHandle(), "", AT_EMPTY_PATH, STATX_TYPE, buf) != 0) return false;
        var mode = BitConverter.ToUInt16(buf, 28); // stx_mode
        return (mode & S_IFMT) == S_IFREG;
    }

    const int AT_EMPTY_PATH = 0x1000;
    const uint STATX_TYPE = 0x1;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, [Out] byte[] buf);
}
