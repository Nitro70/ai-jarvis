using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Tools;

// Port of tools/windows_apps.py. Lets the LLM launch any app installed in the
// Windows Start Menu by name, using fuzzy matching against the AppsFolder
// shell namespace.
//
// The Python version shells out to PowerShell `Get-StartApps`. We enumerate
// the same data directly via the Shell.Application COM (`shell:AppsFolder`)
// using late-bound `dynamic` so we don't need a tlbimp'd Shell32 reference.
//
// Launching uses `explorer.exe shell:AppsFolder\<AppUserModelID>`, which is
// the documented way to activate a Start Menu app from its AUMID and works
// for both Win32 and packaged (UWP/MSIX) apps.

/// <summary>
/// Launch an app installed on the user's PC by name. Fuzzy-matches against
/// the Windows Start Menu via the AppsFolder shell namespace. Windows-only;
/// constructing the tool on a non-Windows OS is fine but Invoke will simply
/// report that it's unavailable. Mirrors the Python <c>open_app</c> tool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppsTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray { "name" },
    };

    private readonly ILogger<WindowsAppsTool> _log;

    // Cache of {DisplayName -> AppUserModelID}. Populated lazily on first
    // invoke; matches Python's module-level _start_apps cache. Enumerating
    // the AppsFolder is not free (50–200 ms typically), so we only do it
    // once per process.
    private Dictionary<string, string>? _startApps;
    private readonly object _cacheLock = new();

    public WindowsAppsTool(ILogger<WindowsAppsTool> log)
    {
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "open_app",
        Description:
            "Launch an app installed on the user's PC by name. Uses fuzzy " +
            "matching against the Windows Start Menu, so partial or " +
            "approximate names work ('chrome', 'discord', 'visual studio').",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return Task.FromResult("App launching by name is currently Windows-only.");

            var name = (arguments["name"]?.GetValue<string>() ?? string.Empty).Trim();
            if (name.Length == 0)
                return Task.FromResult("No app name provided.");

            var apps = LoadStartApps();
            if (apps.Count == 0)
                return Task.FromResult("Can't read the Start Menu — app launching is unavailable.");

            var matched = MatchApp(name, apps);
            if (matched is null)
                return Task.FromResult($"No installed app matching '{name}'.");

            var appid = apps[matched];
            _log.LogInformation("open_app: {Name!r} -> {Matched} ({AppId})", name, matched, appid);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $@"shell:AppsFolder\{appid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult($"Failed to launch {matched}: {ex.Message}");
            }

            return Task.FromResult($"Opened {matched}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "open_app failed");
            return Task.FromResult($"Tool error: {ex.Message}");
        }
    }

    // --- enumeration -------------------------------------------------------

    private Dictionary<string, string> LoadStartApps()
    {
        if (_startApps is not null) return _startApps;
        lock (_cacheLock)
        {
            if (_startApps is not null) return _startApps;
            _startApps = EnumerateStartApps();
            _log.LogInformation("loaded {Count} Start Menu apps", _startApps.Count);
            return _startApps;
        }
    }

    /// <summary>
    /// Enumerate the contents of <c>shell:AppsFolder</c> using the
    /// Shell.Application COM via late-bound <c>dynamic</c>. Returns a map of
    /// display name to AppUserModelID (the same shape Python's Get-StartApps
    /// produces). Empty dict on any failure.
    /// </summary>
    private Dictionary<string, string> EnumerateStartApps()
    {
        // Use ordinal-ignore-case so casing variants like "Chrome" vs
        // "chrome" don't end up as separate entries. Python's dict happens to
        // be exact-match keyed, but the matcher lowercases anyway, so this is
        // an improvement, not a deviation.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        dynamic? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                _log.LogWarning("Shell.Application COM type unavailable");
                return result;
            }
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                _log.LogWarning("failed to create Shell.Application instance");
                return result;
            }

            dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
            if (appsFolder is null)
            {
                _log.LogWarning("shell:AppsFolder NameSpace returned null");
                return result;
            }

            dynamic items = appsFolder.Items();
            // FolderItems supports IEnumerable via COM; foreach over `dynamic`
            // dispatches GetEnumerator() at runtime which works correctly.
            foreach (dynamic item in items)
            {
                string? name = null;
                string? appId = null;
                try
                {
                    name = item.Name as string;
                    // For AppsFolder items, .Path is the AppUserModelID.
                    appId = item.Path as string;
                }
                catch
                {
                    // A bad item shouldn't kill the whole enumeration.
                }
                finally
                {
                    if (item is not null && Marshal.IsComObject(item))
                        Marshal.ReleaseComObject(item);
                }

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(appId))
                {
                    // First write wins, matching Python's dict-comprehension
                    // iteration order semantics (last write wins there, but
                    // duplicates are vanishingly rare and either choice is
                    // launchable).
                    result[name!] = appId!;
                }
            }

            if (Marshal.IsComObject(items)) Marshal.ReleaseComObject(items);
            if (Marshal.IsComObject(appsFolder)) Marshal.ReleaseComObject(appsFolder);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to enumerate Start Menu apps");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }

        return result;
    }

    // --- fuzzy matching ----------------------------------------------------

    /// <summary>
    /// Same algorithm as Python's <c>_match_app</c>:
    /// 1. case-insensitive exact match,
    /// 2. shortest case-insensitive prefix match,
    /// 3. shortest case-insensitive substring match,
    /// 4. closest fuzzy match (ratio >= 0.6).
    /// Returns null if nothing meets any tier.
    /// </summary>
    internal static string? MatchApp(string name, IReadOnlyDictionary<string, string> apps)
    {
        var nameLower = name.ToLowerInvariant();
        var names = apps.Keys.ToList();

        foreach (var n in names)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return n;

        string? best = null;
        foreach (var n in names)
        {
            if (n.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                if (best is null || n.Length < best.Length) best = n;
            }
        }
        if (best is not null) return best;

        foreach (var n in names)
        {
            if (n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (best is null || n.Length < best.Length) best = n;
            }
        }
        if (best is not null) return best;

        // Final tier: difflib.get_close_matches(name, names, n=1, cutoff=0.6).
        // difflib uses SequenceMatcher.ratio() which is 2*M / T where M is
        // the number of matches and T is the total length. We use the same
        // ratio defined below.
        double bestRatio = 0.6;  // cutoff
        string? bestFuzzy = null;
        foreach (var n in names)
        {
            // Python's get_close_matches does a quick-ratio prefilter then
            // computes the real ratio. We just compute the ratio directly —
            // the candidate list is small (a few hundred items) so the
            // optimization doesn't matter.
            var r = SequenceRatio(name, n);
            if (r >= bestRatio)
            {
                bestRatio = r;
                bestFuzzy = n;
            }
        }
        return bestFuzzy;
    }

    /// <summary>
    /// Computes the same ratio as Python's difflib.SequenceMatcher.ratio():
    /// 2 * M / T, where M is the number of matched characters in the longest
    /// common subsequences and T is the total length of both strings.
    /// Implementation uses the same greedy matching-blocks algorithm.
    /// </summary>
    private static double SequenceRatio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        // difflib.get_close_matches is case-sensitive by default, but every
        // caller in our matcher passes the same casing the user typed. For
        // the prior tiers we already case-fold, so by the time we reach fuzzy
        // most easy matches are gone. Mirror Python's case-sensitivity here.
        int matched = MatchedChars(a, b, 0, a.Length, 0, b.Length);
        return 2.0 * matched / (a.Length + b.Length);
    }

    /// <summary>
    /// Recursive helper that mirrors SequenceMatcher.get_matching_blocks:
    /// find the longest matching contiguous run, then recurse on the left
    /// and right slices. Returns the total number of matched characters.
    /// </summary>
    private static int MatchedChars(string a, string b, int alo, int ahi, int blo, int bhi)
    {
        // Find the longest common contiguous substring within a[alo:ahi] and
        // b[blo:bhi]. O((ahi-alo) * (bhi-blo)) DP — fine for tiny inputs.
        int bestI = alo, bestJ = blo, bestSize = 0;
        if (ahi <= alo || bhi <= blo) return 0;

        int aLen = ahi - alo;
        int bLen = bhi - blo;
        // Rolling row to keep memory O(bLen).
        var prev = new int[bLen + 1];
        var curr = new int[bLen + 1];
        for (int i = 0; i < aLen; i++)
        {
            for (int j = 0; j < bLen; j++)
            {
                if (a[alo + i] == b[blo + j])
                {
                    curr[j + 1] = prev[j] + 1;
                    if (curr[j + 1] > bestSize)
                    {
                        bestSize = curr[j + 1];
                        bestI = alo + i - bestSize + 1;
                        bestJ = blo + j - bestSize + 1;
                    }
                }
                else
                {
                    curr[j + 1] = 0;
                }
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr);
        }

        if (bestSize == 0) return 0;
        return bestSize
            + MatchedChars(a, b, alo, bestI, blo, bestJ)
            + MatchedChars(a, b, bestI + bestSize, ahi, bestJ + bestSize, bhi);
    }
}
