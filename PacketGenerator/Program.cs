using PacketGenerator;

try
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: PacketGenerator <definition.json> <solution-root>");
        return 1;
    }

    IReadOnlyList<string> paths = PacketGeneratorEngine.Generate(args[0], args[1]);
    Console.WriteLine($"Generated {paths.Count} files.");
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
