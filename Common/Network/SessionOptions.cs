namespace Common.Network
{
    public sealed record SessionOptions
    {
        public int MaxPendingPackets { get; init; } = 1024;
        public int MaxPendingBytes { get; init; } = 1024 * 1024;
    }
}
