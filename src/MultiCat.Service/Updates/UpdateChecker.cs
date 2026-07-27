using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MultiCat.Service.Updates;

/// <summary>
/// Asks GitHub occasionally whether a newer release exists, so a station is not
/// quietly running a version with a fix in it. It only ever reports — nothing is
/// downloaded or installed, because doing that would drop the radio and every app
/// connected to it, which is the operator's call to make.
/// </summary>
public sealed class UpdateChecker(ILogger<UpdateChecker> logger, IConfiguration configuration) : IHostedService, IDisposable
{
    // Deliberately the list, not "releases/latest": that endpoint excludes
    // pre-releases, and every MultiCAT release so far is one, so it answers 404.
    // The list comes back newest first and includes them.
    private const string ReleasesUrl = "https://api.github.com/repos/gsa700/MultiCat/releases?per_page=10";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>The newest version seen, or null while none is known.</summary>
    public string? LatestVersion { get; private set; }

    public string? ReleaseUrl { get; private set; }

    public string? ReleaseNotes { get; private set; }

    /// <summary>True when the newest release is ahead of what is running.</summary>
    public bool UpdateAvailable { get; private set; }

    public string RunningVersion { get; } =
        typeof(UpdateChecker).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Updates:CheckOnline", true))
        {
            logger.LogInformation("Update checking is switched off");
            return Task.CompletedTask;
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"MultiCAT/{RunningVersion}");
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // A little after start, then daily — a station app has no business polling
        // a web service more often than that.
        var delay = TimeSpan.FromSeconds(20);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await CheckAsync(ct);
            delay = TimeSpan.FromHours(24);
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var releases = await _http.GetFromJsonAsync<GithubRelease[]>(ReleasesUrl, ct);
            var release = releases?.FirstOrDefault(r => !r.Draft);
            if (release?.TagName is not { Length: > 0 } tag)
            {
                return;
            }

            LatestVersion = tag.TrimStart('v', 'V');
            ReleaseUrl = release.HtmlUrl;
            ReleaseNotes = release.Name;
            UpdateAvailable = IsNewer(LatestVersion, RunningVersion);

            if (UpdateAvailable)
            {
                logger.LogInformation(
                    "MultiCAT {Latest} is available (running {Running}): {Url}", LatestVersion, RunningVersion, ReleaseUrl);
            }
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, or GitHub having a moment: never worth a fuss.
            logger.LogDebug("Update check failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Compares dotted versions numerically, so 0.3.10 counts as newer than 0.3.9 —
    /// a string comparison would get that backwards. A pre-release suffix is ignored
    /// for ordering; only the numbers decide.
    /// </summary>
    public static bool IsNewer(string candidate, string running)
    {
        var left = Numbers(candidate);
        var right = Numbers(running);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r)
            {
                return l > r;
            }
        }

        return false;
    }

    private static int[] Numbers(string version)
    {
        var core = version.Split('-')[0];
        return [.. core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0)];
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Dispose();
        _http.Dispose();
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }
}
