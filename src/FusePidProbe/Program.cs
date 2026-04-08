using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FuseSharp;
using Mono.Unix.Native;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/FusePidProbe -- <mountpoint>");
    Console.Error.WriteLine("Example: dotnet run --project src/FusePidProbe -- /tmp/fuse-pid-probe");
    return;
}

var mountPoint = Path.GetFullPath(args[0]);
Directory.CreateDirectory(mountPoint);

var actualArgs = new[] { "-s", "-f", mountPoint };
using var fs = new ProbeFileSystem();
using var handler = new FileSystemHandler(fs, actualArgs);

Console.WriteLine($"Mounting test filesystem at {mountPoint}");
Console.WriteLine("Press Ctrl+C to stop.");
var rc = handler.Start();
Console.WriteLine($"FuseSharp exited with rc={rc}");

sealed class ProbeFileSystem : FileSystem
{
    private const string FileName = "/probe.txt";
    private static readonly byte[] Content = Encoding.UTF8.GetBytes(
        "FusePidProbe\n" +
        "Read this file to trigger PID/UID/GID/process inspection.\n");

    public override Errno OnGetPathStatus(string path, out Stat st)
    {
        st = new Stat();
        if (path == "/")
        {
            st.st_mode = FilePermissions.S_IFDIR | FilePermissions.ACCESSPERMS;
            st.st_nlink = 2;
            return 0;
        }

        if (path == FileName)
        {
            st.st_mode = FilePermissions.S_IFREG | FilePermissions.DEFFILEMODE;
            st.st_nlink = 1;
            st.st_size = Content.Length;
            return 0;
        }

        return Errno.ENOENT;
    }

    public override Errno OnReadDirectory(string directory, PathInfo info, out IEnumerable<DirectoryEntry> paths)
    {
        if (directory != "/")
        {
            paths = Array.Empty<DirectoryEntry>();
            return Errno.ENOENT;
        }

        LogAccess("readdir", directory);
        paths = new[]
        {
            new DirectoryEntry("."),
            new DirectoryEntry(".."),
            new DirectoryEntry(FileName.TrimStart('/')),
        };
        return 0;
    }

    public override Errno OnOpenHandle(string file, PathInfo info)
    {
        if (file != FileName)
            return Errno.ENOENT;

        LogAccess("open", file);
        return 0;
    }

    public override Errno OnReadHandle(string file, PathInfo info, byte[] buf, long offset, out int bytesRead)
    {
        bytesRead = 0;
        if (file != FileName)
            return Errno.ENOENT;

        LogAccess("read", file);
        if (offset >= Content.Length)
            return 0;

        var count = Math.Min(buf.Length, Content.Length - (int)offset);
        Array.Copy(Content, offset, buf, 0, count);
        bytesRead = count;
        return 0;
    }

    private void LogAccess(string operation, string path)
    {
        var ctx = FuseSharp.Process.GetContext();
        var pid = ctx.ProcessId;
        var uid = (uint)ctx.UserId;
        var gid = (uint)ctx.GroupId;

        var exePath = TryGetExecutablePath(pid) ?? "<unknown>";
        var signature = GetCodeSignatureSummary(exePath);
        var parentTree = string.Join(" -> ", GetParentTree(pid));

        Console.WriteLine("=== Fuse access ===");
        Console.WriteLine($"op={operation} path={path}");
        Console.WriteLine($"pid={pid} uid={uid} gid={gid}");
        Console.WriteLine("gid means Unix group id of the calling process.");
        Console.WriteLine($"exe={exePath}");
        Console.WriteLine($"signature={signature}");
        Console.WriteLine($"parentTree={parentTree}");
        Console.WriteLine();
    }

    private static string? TryGetExecutablePath(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.MainModule?.FileName;
        }
        catch
        {
            return TryGetExecutablePathPs(pid);
        }
    }

    private static string? TryGetExecutablePathPs(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("ps", $"-p {pid} -o comm=")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static string GetCodeSignatureSummary(string exePath)
    {
        try
        {
            if (!File.Exists(exePath))
                return "not-a-file";

            var cert = X509Certificate.CreateFromSignedFile(exePath);
            return string.IsNullOrWhiteSpace(cert.Subject) ? "signed (unknown subject)" : cert.Subject;
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo("codesign", $"-dv --verbose=2 \"{exePath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) return "unsigned-or-unreadable";
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                var line = err.Split('\n').FirstOrDefault(l => l.Contains("Authority=") || l.Contains("Identifier="));
                return string.IsNullOrWhiteSpace(line) ? "unsigned-or-unreadable" : line.Trim();
            }
            catch
            {
                return "unsigned-or-unreadable";
            }
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
            if (ppid <= 0 || ppid == current)
                break;
            current = ppid;
        }

        return result;
    }

    private static (string name, int ppid) GetProcessInfo(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("ps", $"-p {pid} -o ppid=,comm=")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return ("<unknown>", -1);
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            if (string.IsNullOrWhiteSpace(output)) return ("<unknown>", -1);
            var parts = output.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var ppid = parts.Length > 0 && int.TryParse(parts[0], out var parsed) ? parsed : -1;
            var name = parts.Length > 1 ? parts[1].Trim() : "<unknown>";
            return (name, ppid);
        }
        catch
        {
            return ("<unknown>", -1);
        }
    }
}
