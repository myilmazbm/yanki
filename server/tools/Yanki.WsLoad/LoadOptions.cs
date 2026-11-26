using System.Globalization;

namespace Yanki.WsLoad;

/// <summary>Command line configuration for the realtime load generator.</summary>
internal sealed class LoadOptions
{
    /// <summary>Placeholder replaced by the zero-based connection index inside <see cref="TokenTemplate"/>.</summary>
    public const string IndexPlaceholder = "{i}";

    public required Uri Url { get; init; }

    public int Connections { get; init; } = 100;

    /// <summary>New connections opened per second.</summary>
    public int RampPerSecond { get; init; } = 100;

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Token, with <c>{i}</c> replaced by the connection index when present.</summary>
    public string TokenTemplate { get; init; } = "dev:load-{i}";

    /// <summary>Skip TLS certificate validation (self-signed on-prem certificates).</summary>
    public bool Insecure { get; init; }

    /// <summary>How often each connection sends an application <c>Ping</c>.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Optional server process id; its working set is sampled for a per-connection figure.</summary>
    public int? TargetPid { get; init; }

    public string TokenFor(int index) =>
        TokenTemplate.Contains(IndexPlaceholder, StringComparison.Ordinal)
            ? TokenTemplate.Replace(IndexPlaceholder, index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            : TokenTemplate;

    public bool SharesOneUser => !TokenTemplate.Contains(IndexPlaceholder, StringComparison.Ordinal);

    public const string Usage = """
        yanki-wsload - Yanki realtime WebSocket load generator (v0)

        Usage:
          yanki-wsload --url wss://host/ws [options]

        Options:
          --url <uri>            Realtime endpoint, ws:// or wss://            (required)
          --connections <n>      Connections to open                          (default 100)
          --ramp <n>/s           Connections opened per second                 (default 100/s)
          --duration <t>         How long to hold the connections, e.g. 30s    (default 30s)
          --token <template>     Session token; '{i}' becomes the connection
                                 index, which spreads the load over separate
                                 users so MaxConnectionsPerUser does not evict
                                 them                                          (default dev:load-{i})
          --ping-interval <t>    Application Ping period                       (default 1s)
          --insecure             Do not validate the server TLS certificate
          --target-pid <pid>     Also sample this process' working set, to get
                                 a memory-per-connection figure for the server
          --help                 Show this help

        Durations accept ms, s or m suffixes (500ms, 30s, 2m); a bare number means seconds.
        """;

    /// <summary>Parses the command line. Returns null and prints the reason when the input is invalid.</summary>
    public static LoadOptions? Parse(string[] args, TextWriter error)
    {
        Uri? url = null;
        int connections = 100;
        int ramp = 100;
        TimeSpan duration = TimeSpan.FromSeconds(30);
        TimeSpan pingInterval = TimeSpan.FromSeconds(1);
        string token = "dev:load-{i}";
        bool insecure = false;
        int? targetPid = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--insecure":
                    insecure = true;
                    continue;
                case "--help" or "-h" or "-?":
                    return null;
            }

            if (i + 1 >= args.Length)
            {
                error.WriteLine($"Missing value for {argument}.");
                return null;
            }

            string value = args[++i];
            switch (argument)
            {
                case "--url":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out url))
                    {
                        error.WriteLine($"'{value}' is not an absolute URL.");
                        return null;
                    }

                    break;

                case "--connections":
                    if (!TryParseCount(value, out connections))
                    {
                        error.WriteLine($"--connections must be a positive integer, got '{value}'.");
                        return null;
                    }

                    break;

                case "--ramp":
                    if (!TryParseCount(value.EndsWith("/s", StringComparison.Ordinal) ? value[..^2] : value, out ramp))
                    {
                        error.WriteLine($"--ramp must look like 100/s, got '{value}'.");
                        return null;
                    }

                    break;

                case "--duration":
                    if (!TryParseDuration(value, out duration))
                    {
                        error.WriteLine($"--duration must look like 30s, got '{value}'.");
                        return null;
                    }

                    break;

                case "--ping-interval":
                    if (!TryParseDuration(value, out pingInterval))
                    {
                        error.WriteLine($"--ping-interval must look like 1s, got '{value}'.");
                        return null;
                    }

                    break;

                case "--token":
                    token = value;
                    break;

                case "--target-pid":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out int pid))
                    {
                        error.WriteLine($"--target-pid must be an integer, got '{value}'.");
                        return null;
                    }

                    targetPid = pid;
                    break;

                default:
                    error.WriteLine($"Unknown option '{argument}'.");
                    return null;
            }
        }

        if (url is null)
        {
            error.WriteLine("--url is required.");
            return null;
        }

        if (url.Scheme is not ("ws" or "wss" or "http" or "https"))
        {
            error.WriteLine($"--url must use ws, wss, http or https, got '{url.Scheme}'.");
            return null;
        }

        // ClientWebSocket only accepts the ws/wss schemes; http(s) is accepted for convenience.
        url = url.Scheme switch
        {
            "http" => new UriBuilder(url) { Scheme = "ws", Port = url.Port }.Uri,
            "https" => new UriBuilder(url) { Scheme = "wss", Port = url.Port }.Uri,
            _ => url,
        };

        return new LoadOptions
        {
            Url = url,
            Connections = connections,
            RampPerSecond = ramp,
            Duration = duration,
            PingInterval = pingInterval,
            TokenTemplate = token,
            Insecure = insecure,
            TargetPid = targetPid,
        };
    }

    private static bool TryParseCount(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0;

    internal static bool TryParseDuration(string value, out TimeSpan result)
    {
        result = default;
        (string suffix, double factor)[] units = [("ms", 1), ("s", 1_000), ("m", 60_000)];

        foreach ((string suffix, double factor) in units)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double scaled)
                && scaled > 0)
            {
                result = TimeSpan.FromMilliseconds(scaled * factor);
                return true;
            }
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
        {
            result = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }
}
