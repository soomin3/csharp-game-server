namespace Common.Network
{
    public sealed record SessionOptions
    {
        public int MaxPendingPackets { get; init; } = 1024;
        public int MaxPendingBytes { get; init; } = 1024 * 1024;
        // 송신 루프가 대기 패킷을 모아 한 번에 쓰는 버퍼 크기. 이보다 큰 패킷은 복사 없이 따로 쓴다.
        public int SendBatchBytes { get; init; } = 8 * 1024;
    }
}
