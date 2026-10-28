namespace LoadClient;

internal sealed record LoadOptions(string Host, int Port, string LoginUrl, int Clients, int DurationSeconds,
    int WarmupSeconds, int IntervalMs, int Rooms, int Seed, string Output)
{
    public static LoadOptions Parse(string[] args)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = { "host", "port", "login-url", "clients", "duration", "warmup", "interval", "rooms", "seed", "output" };
        for (int index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--") || index + 1 >= args.Length || !allowed.Contains(args[index][2..]) ||
                !values.TryAdd(args[index][2..], args[index + 1])) throw new ArgumentException("Use unique --name value options.");
        }
        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback, int min, int max)
        {
            if (!int.TryParse(Get(key, fallback.ToString()), out int value) || value < min || value > max)
                throw new ArgumentException($"Invalid --{key}: expected {min}..{max}.");
            return value;
        }
        string loginUrl = Get("login-url", "http://127.0.0.1:5000/login");
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Invalid --login-url.");
        return new LoadOptions(Get("host", "127.0.0.1"), Number("port", 7777, 1, 65535), loginUrl,
            Number("clients", 500, 1, 5000), Number("duration", 10, 1, 3600), Number("warmup", 2, 0, 60),
            Number("interval", 100, 10, 10000), Number("rooms", 3, 1, 100), Number("seed", 42, 0, int.MaxValue),
            Path.GetFullPath(Get("output", "artifacts/load-client-result.json")));
    }
}
