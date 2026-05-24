using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Setup.Services;

/// <summary>
/// Finds a compatible Python (3.10-3.13) or silently installs Python 3.12.
/// C# port of the logic in run.bat.
/// </summary>
public static class PythonInstaller
{
    private const string PyVersion = "3.12.8";
    private const string PyDownloadUrl = "https://www.python.org/ftp/python/3.12.8/python-3.12.8-amd64.exe";

    public static string ExpectedUserInstallPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "python.exe");

    /// <summary>Find an existing compatible python.exe on disk, or null.</summary>
    public static string? FindExisting()
    {
        // 1. Previously auto-installed by us.
        if (TryAccept(ExpectedUserInstallPath, out _)) return ExpectedUserInstallPath;

        // 2. `where python`
        foreach (var p in WhereOnPath("python.exe"))
        {
            if (p.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
                continue; // Microsoft Store stub
            if (TryAccept(p, out _)) return p;
        }

        // 3. `py -X.Y` launcher
        foreach (var ver in new[] { "3.12", "3.13", "3.11", "3.10" })
        {
            var path = QueryPyLauncher(ver);
            if (path != null && TryAccept(path, out _)) return path;
        }

        return null;
    }

    /// <summary>Run the Python 3.12 installer silently. Returns path on success.</summary>
    public static async Task<string> InstallAsync(
        IProgress<string>? log = null,
        IProgress<double>? percent = null,
        CancellationToken ct = default)
    {
        var installerPath = Path.Combine(Path.GetTempPath(), $"jarvis-python-{PyVersion}.exe");

        log?.Report($"Downloading Python {PyVersion}...");
        await DownloadFileAsync(PyDownloadUrl, installerPath, percent, ct);

        log?.Report($"Running Python {PyVersion} installer (silent, per-user)...");
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            // Per-user install -> no UAC. PrependPath so future shells see it.
            Arguments = "/quiet InstallAllUsers=0 PrependPath=1 Include_test=0 Include_launcher=0 Shortcuts=0 AssociateFiles=0",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
                        ?? throw new InvalidOperationException("Failed to start Python installer.");
        await proc.WaitForExitAsync(ct);
        try { File.Delete(installerPath); } catch { }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Python installer exited with code {proc.ExitCode}.");

        if (!File.Exists(ExpectedUserInstallPath))
            throw new InvalidOperationException(
                $"Installer reported success but python.exe not found at {ExpectedUserInstallPath}.");

        log?.Report($"Python {PyVersion} installed.");
        return ExpectedUserInstallPath;
    }

    /// <summary>Run `python -m pip install -r requirements-all.txt` in the given dir.</summary>
    public static async Task RunPipInstallAsync(
        string pythonExe,
        string workingDir,
        string requirementsFile,
        IProgress<string>? log,
        CancellationToken ct = default)
    {
        // Upgrade pip first (silently) to dodge cache-deserialization warnings.
        log?.Report("Upgrading pip...");
        await RunProcessAsync(pythonExe,
            "-m pip install --disable-pip-version-check --upgrade pip",
            workingDir, log: null, ct: ct, ignoreExitCode: true);

        log?.Report($"Installing Python packages from {requirementsFile} (this may take several minutes)...");
        await RunProcessAsync(pythonExe,
            $"-m pip install --disable-pip-version-check -r \"{requirementsFile}\"",
            workingDir, log, ct);
    }

    // ===================================================================
    // helpers
    // ===================================================================

    private static bool TryAccept(string exe, out (int maj, int min) version)
    {
        version = (0, 0);
        try
        {
            if (!File.Exists(exe)) return false;
            var output = RunProcessSync(exe, "--version", out var rc);
            if (rc != 0) return false;
            // "Python 3.12.8"
            var m = Regex.Match(output, @"Python\s+(\d+)\.(\d+)");
            if (!m.Success) return false;
            int maj = int.Parse(m.Groups[1].Value);
            int min = int.Parse(m.Groups[2].Value);
            version = (maj, min);
            return maj == 3 && min >= 10 && min <= 13;
        }
        catch { return false; }
    }

    private static System.Collections.Generic.IEnumerable<string> WhereOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir, exe); } catch { continue; }
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    private static string? QueryPyLauncher(string version)
    {
        try
        {
            var output = RunProcessSync("py", $"-{version} -c \"import sys;print(sys.executable)\"", out var rc);
            if (rc != 0) return null;
            var path = output.Trim();
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static string RunProcessSync(string file, string args, out int exitCode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        exitCode = p.ExitCode;
        return string.IsNullOrEmpty(stdout) ? stderr : stdout;
    }

    private static async Task RunProcessAsync(
        string file, string args, string workingDir,
        IProgress<string>? log, CancellationToken ct, bool ignoreExitCode = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Report(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) log?.Report(e.Data); };
        if (!p.Start()) throw new InvalidOperationException($"Failed to start {file}");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        if (!ignoreExitCode && p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited with code {p.ExitCode}.");
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<double>? percent, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) percent?.Report(100.0 * read / total);
        }
    }
}
