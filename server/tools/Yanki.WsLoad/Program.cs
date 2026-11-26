using Yanki.WsLoad;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
{
    Console.WriteLine(LoadOptions.Usage);
    return 0;
}

LoadOptions? options = LoadOptions.Parse(args, Console.Error);
if (options is null)
{
    Console.WriteLine(LoadOptions.Usage);
    return 2;
}

if (options.SharesOneUser && options.Connections > 1)
{
    Console.WriteLine(
        $"Warning: every connection uses the token '{options.TokenTemplate}', so they all map to one user. " +
        "The server evicts the oldest connections beyond Yanki:Realtime:MaxConnectionsPerUser. " +
        "Use a token containing '{i}' to spread the load over separate users.");
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

Console.WriteLine(
    $"yanki-wsload -> {options.Url}: {options.Connections} connections, ramp {options.RampPerSecond}/s, " +
    $"hold {options.Duration.TotalSeconds:F0} s, ping every {options.PingInterval.TotalMilliseconds:F0} ms.");

var runner = new LoadRunner(options, Console.Out);
LoadReport report;
try
{
    report = await runner.RunAsync(cancellation.Token);
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Interrupted.");
    return 130;
}

report.Write(Console.Out);
return report.ConnectSuccessPercent >= 99.0 ? 0 : 1;
