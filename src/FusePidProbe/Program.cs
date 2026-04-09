using FuseDotNet;
using FuseDotNet.Extensions;
using LTRData.Extensions.Native.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

#if NET5_0_OR_GREATER
[SupportedOSPlatform("macos")]
#endif
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: dotnet run --project src/FusePidProbe -- <mountpoint>");
            Console.Error.WriteLine("Example: dotnet run --project src/FusePidProbe -- /tmp/fuse-pid-probe");
            return 1;
        }

        var mountPoint = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(mountPoint);

        var fuseArgs = new[] { "-f", "-s", mountPoint };
        using var service = new FuseService(new ProbeOperations(), fuseArgs);
        Console.WriteLine($"Mounting test filesystem at {mountPoint}");
        Console.WriteLine("Running in foreground (-f), press Ctrl+C to stop.");
        service.Start();
        return 0;
    }
}

#if NET5_0_OR_GREATER
[SupportedOSPlatform("macos")]
#endif
internal sealed class ProbeOperations : IFuseOperations
{
    private const string ProbePath = "/probe.txt";
    private static readonly byte[] Content = "FusePidProbe\nRead this file to trigger PID/UID/GID/process inspection.\n"u8.ToArray();

    private readonly PosixFileMode dirMode = PosixFileMode.Directory
        | PosixFileMode.OwnerAll
        | PosixFileMode.GroupReadExecute
        | PosixFileMode.OthersReadExecute;

    private readonly PosixFileMode fileMode = PosixFileMode.Regular
        | PosixFileMode.OwnerRead
        | PosixFileMode.GroupRead
        | PosixFileMode.OthersRead;

    public void Init(ref FuseConnInfo fuse_conn_info)
        => Console.WriteLine($"Initializing file system, driver capabilities: {fuse_conn_info.capable}, requested: {fuse_conn_info.want}");

    public PosixResult GetAttr(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo)
    {
        var fileName = FuseHelper.GetString(fileNamePtr);
        stat = default;

        if (fileName == "/")
        {
            stat.st_mode = dirMode;
            stat.st_nlink = 2;
            stat.st_atim = stat.st_mtim = stat.st_ctim = stat.st_birthtim = TimeSpec.Now();
            return PosixResult.Success;
        }

        if (fileName == ProbePath)
        {
            stat.st_mode = fileMode;
            stat.st_nlink = 1;
            stat.st_size = Content.LongLength;
            stat.st_atim = stat.st_mtim = stat.st_ctim = stat.st_birthtim = TimeSpec.Now();
            return PosixResult.Success;
        }

        return PosixResult.ENOENT;
    }

    public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var fileName = FuseHelper.GetString(fileNamePtr);
        if (fileName != "/") return PosixResult.ENOENT;
        LogAccess("opendir", fileName, fileInfo);
        return PosixResult.Success;
    }

    public PosixResult ReadDir(ReadOnlyNativeMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags)
    {
        var fileName = FuseHelper.GetString(fileNamePtr);
        if (fileName != "/")
        {
            entries = Array.Empty<FuseDirEntry>();
            return PosixResult.ENOENT;
        }

        LogAccess("readdir", fileName, fileInfo);
        entries = EnumerateEntries();
        return PosixResult.Success;
    }

    public PosixResult Open(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var fileName = FuseHelper.GetString(fileNamePtr);
        if (fileName != ProbePath) return PosixResult.ENOENT;
        LogAccess("open", fileName, fileInfo);
        return PosixResult.Success;
    }

    public PosixResult Read(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> buffer, long position, out int readLength, ref FuseFileInfo fileInfo)
    {
        var fileName = FuseHelper.GetString(fileNamePtr);
        readLength = 0;
        if (fileName != ProbePath) return PosixResult.ENOENT;

        LogAccess("read", fileName, fileInfo);
        if (position >= Content.Length)
            return PosixResult.Success;

        var count = Math.Min(buffer.Length, Content.Length - (int)position);
        Content.AsSpan((int)position, count).CopyTo(buffer.Span);
        readLength = count;
        return PosixResult.Success;
    }

    public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask)
        => FuseHelper.GetString(fileNamePtr) is "/" or ProbePath ? PosixResult.Success : PosixResult.ENOENT;

    public PosixResult StatFs(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
    {
        statvfs = default;
        return PosixResult.Success;
    }

    public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult ReadLink(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> target) => PosixResult.ENOENT;
    public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode) => PosixResult.EROFS;
    public PosixResult Release(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr) => PosixResult.EROFS;
    public PosixResult FSync(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr) => PosixResult.EROFS;
    public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.EROFS;
    public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.EROFS;
    public PosixResult Truncate(ReadOnlyNativeMemory<byte> fileNamePtr, long size) => PosixResult.EROFS;
    public PosixResult Write(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> buffer, long position, out int writtenLength, ref FuseFileInfo fileInfo) { writtenLength = 0; return PosixResult.EROFS; }
    public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.EROFS;
    public PosixResult Flush(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult UTime(ReadOnlyNativeMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo) => PosixResult.EROFS;
    public PosixResult IoCtl(ReadOnlyNativeMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo, FuseIoctlFlags flags, nint data) => PosixResult.ENOSYS;
    public PosixResult ChMod(NativeMemory<byte> fileNamePtr, PosixFileMode mode) => PosixResult.EROFS;
    public PosixResult ChOwn(NativeMemory<byte> fileNamePtr, int uid, int gid) => PosixResult.EROFS;
    public PosixResult FAllocate(NativeMemory<byte> fileNamePtr, FuseAllocateMode mode, long offset, long length, ref FuseFileInfo fileInfo) => PosixResult.ENOTSUP;
    public PosixResult ChFlags(ReadOnlyNativeMemory<byte> fileNamePtr, ulong flags) => PosixResult.ENOSYS;
    public PosixResult SetXAttr(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> namePtr, ReadOnlyNativeMemory<byte> value, uint size, int flags, uint position) => PosixResult.ENOSYS;
    public PosixResult GetXAttr(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> namePtr, NativeMemory<byte> value, uint size, out int bytesWritten) { bytesWritten = 0; return PosixResult.ENOENT; }
    public PosixResult ListXAttr(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> list, uint size, out int bytesWritten) { bytesWritten = 0; return PosixResult.Success; }
    public PosixResult RemoveXAttr(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> namePtr) => PosixResult.ENOSYS;
    public PosixResult BMap(ReadOnlyNativeMemory<byte> fileNamePtr, long blocksize, out ulong idx) { idx = 0; return PosixResult.ENOSYS; }
    public object? Destroy(object? private_data) => private_data;
    public void Dispose() { }

    private void LogAccess(string operation, string path, FuseFileInfo fileInfo)
    {
        var pid = GetIntProperty(fileInfo.Context, "Pid") ?? GetIntProperty(fileInfo.Context, "ProcessId") ?? -1;
        var uid = GetUIntProperty(fileInfo.Context, "Uid") ?? GetUIntProperty(fileInfo.Context, "UserId") ?? 0;
        var gid = GetUIntProperty(fileInfo.Context, "Gid") ?? GetUIntProperty(fileInfo.Context, "GroupId") ?? 0;
        var exePath = pid > 0 ? TryGetExecutablePath(pid) ?? "<unknown>" : "<unknown>";
        var signature = GetCodeSignatureSummary(exePath);
        var parentTree = pid > 0 ? string.Join(" -> ", GetParentTree(pid)) : "<unknown>";

        Console.WriteLine("=== Fuse access ===");
        Console.WriteLine($"op={operation} path={path}");
        Console.WriteLine($"pid={pid} uid={uid} gid={gid}");
        Console.WriteLine("gid means Unix group id of the calling process.");
        Console.WriteLine($"exe={exePath}");
        Console.WriteLine($"signature={signature}");
        Console.WriteLine($"parentTree={parentTree}");
        Console.WriteLine();
    }

    private static IEnumerable<FuseDirEntry> EnumerateEntries()
    {
        yield return new(Name: ".", Offset: 0, Flags: 0, Stat: new() { st_mode = PosixFileMode.Directory });
        yield return new(Name: "..", Offset: 0, Flags: 0, Stat: new() { st_mode = PosixFileMode.Directory });
        yield return new(Name: "probe.txt", Offset: 0, Flags: 0, Stat: new() { st_mode = PosixFileMode.Regular });
    }

    private static int? GetIntProperty(object? obj, string name)
    {
        if (obj is null) return null;
        var p = obj.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        var value = p?.GetValue(obj);
        if (value is int i) return i;
        if (value is uint ui) return (int)ui;
        return null;
    }

    private static uint? GetUIntProperty(object? obj, string name)
    {
        if (obj is null) return null;
        var p = obj.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        var value = p?.GetValue(obj);
        if (value is uint ui) return ui;
        if (value is int i) return (uint)i;
        return null;
    }

    private static string? TryGetExecutablePath(int pid)
    {
        try { using var proc = Process.GetProcessById(pid); return proc.MainModule?.FileName; }
        catch { return TryGetExecutablePathPs(pid); }
    }

    private static string? TryGetExecutablePathPs(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("ps", $"-p {pid} -o comm=") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch { return null; }
    }

    private static string GetCodeSignatureSummary(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return "not-a-file";
            var cert = X509Certificate.CreateFromSignedFile(exePath);
            return string.IsNullOrWhiteSpace(cert.Subject) ? "signed (unknown subject)" : cert.Subject;
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo("codesign", $"-dv --verbose=2 \"{exePath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                if (p is null) return "unsigned-or-unreadable";
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                var line = err.Split('\n').FirstOrDefault(l => l.Contains("Authority=") || l.Contains("Identifier="));
                return string.IsNullOrWhiteSpace(line) ? "unsigned-or-unreadable" : line.Trim();
            }
            catch { return "unsigned-or-unreadable"; }
        }
    }

    private static IEnumerable<string> GetParentTree(int pid)
    {
        var result = new List<string>();
        var current = pid;
        var seen = new HashSet<int>();
        while (current > 0 && seen.Add(current))
        {
            var (name, ppid) = GetProcessInfo(current);
            result.Add($"{current}:{name}");
            if (ppid <= 0 || ppid == current) break;
            current = ppid;
        }
        return result;
    }

    private static (string name, int ppid) GetProcessInfo(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("ps", $"-p {pid} -o ppid=,comm=") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return ("<unknown>", -1);
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            if (string.IsNullOrWhiteSpace(output)) return ("<unknown>", -1);
            var parts = output.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var ppid = parts.Length > 0 && int.TryParse(parts[0], out var parsed) ? parsed : -1;
            var name = parts.Length > 1 ? parts[1].Trim() : "<unknown>";
            return (name, ppid);
        }
        catch { return ("<unknown>", -1); }
    }
}
