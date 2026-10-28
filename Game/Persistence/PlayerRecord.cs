namespace Game.Persistence;

public sealed record PlayerRecord(string Name, float X, float Y, int RoomId = 1);

public interface IPlayerStore : IAsyncDisposable
{
    Task<PlayerRecord?> LoadAsync(string name);
    Task SaveAsync(PlayerRecord player);
    Task DrainAsync();
}
