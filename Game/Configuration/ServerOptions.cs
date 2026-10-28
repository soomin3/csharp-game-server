namespace Game.Configuration;

public sealed record ServerOptions(int Port, int Workers, int Rooms, string DataPath, string LogPath,
    int ShutdownAfterSeconds, int SendPackets, int SendBytes, bool Quiet)
{
    public static ServerOptions Parse(string[] args)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = { "port", "workers", "rooms", "data", "log", "shutdown-after", "send-packets", "send-bytes" };
        bool quiet = false;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--quiet")
            {
                if (quiet) throw new ArgumentException("Duplicate --quiet.");
                quiet = true;
                continue;
            }
            if (!args[index].StartsWith("--") || !allowed.Contains(args[index][2..]) || index + 1 >= args.Length || args[index + 1].StartsWith("--") ||
                !values.TryAdd(args[index][2..], args[++index])) throw new ArgumentException("Use unique --name value options.");
        }
        int Number(string name, int fallback, int min, int max)
        {
            if (!int.TryParse(values.GetValueOrDefault(name, fallback.ToString()), out int value) || value < min || value > max)
                throw new ArgumentException($"--{name}: expected {min}..{max}.");
            return value;
        }
        string data = Path.GetFullPath(values.GetValueOrDefault("data", Path.Combine(AppContext.BaseDirectory, "data", "players.db")));
        string log = Path.GetFullPath(values.GetValueOrDefault("log", Path.Combine(AppContext.BaseDirectory, "logs", $"game-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log")));
        if (string.Equals(data, log, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("--data and --log must be different files.");
        return new ServerOptions(Number("port", 7777, 1, 65535), Number("workers", 2, 1, 64), Number("rooms", 3, 1, 100),
            data, log, Number("shutdown-after", 0, 0, 86400), Number("send-packets", 1024, 1, 65536),
            Number("send-bytes", 1024 * 1024, 64, 64 * 1024 * 1024), quiet);
    }
}
