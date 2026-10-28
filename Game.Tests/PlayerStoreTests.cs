using Game.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Game.Tests;

public sealed class PlayerStoreTests
{
    [Fact]
    public async Task 새_저장소_인스턴스에서도_마지막_위치와_방을_복원한다()
    {
        using TempDatabase database = new TempDatabase();
        await using (SqlitePlayerStore store = new SqlitePlayerStore(database.Path))
        {
            Assert.Null(await store.LoadAsync("김철수"));
            await store.SaveAsync(new PlayerRecord("김철수", 3.5f, 7f, 2));
        }
        await using SqlitePlayerStore reopened = new SqlitePlayerStore(database.Path);
        Assert.Equal(new PlayerRecord("김철수", 3.5f, 7f, 2), await reopened.LoadAsync("김철수"));
    }

    [Fact]
    public async Task 저장을_기다리지_않고_재조회해도_먼저_요청한_저장이_반영된다()
    {
        using TempDatabase database = new TempDatabase();
        await using SqlitePlayerStore store = new SqlitePlayerStore(database.Path);
        Task first = store.SaveAsync(new PlayerRecord("Kim", 1, 1));
        Task last = store.SaveAsync(new PlayerRecord("Kim", 2, 3));
        Task<PlayerRecord?> read = store.LoadAsync("Kim");
        await Task.WhenAll(first, last);
        Assert.Equal(new PlayerRecord("Kim", 2, 3), await read);
    }

    [Fact]
    public async Task SQL처럼_생긴_이름도_문자열로만_저장한다()
    {
        using TempDatabase database = new TempDatabase();
        await using SqlitePlayerStore store = new SqlitePlayerStore(database.Path);
        string name = "Kim'; DROP TABLE players;--";
        await store.SaveAsync(new PlayerRecord(name, 4, 5));
        Assert.Equal(name, (await store.LoadAsync(name))!.Name);
        await store.SaveAsync(new PlayerRecord("Lee", 1, 2));
        Assert.NotNull(await store.LoadAsync("Lee"));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(101f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public async Task 잘못된_위치는_저장하지_않는다(float x)
    {
        using TempDatabase database = new TempDatabase();
        await using SqlitePlayerStore store = new SqlitePlayerStore(database.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(new PlayerRecord("Kim", x, 0)));
        Assert.Null(await store.LoadAsync("Kim"));
    }

    [Fact]
    public async Task 방_번호는_1부터_시작한다()
    {
        using TempDatabase database = new TempDatabase();
        await using SqlitePlayerStore store = new SqlitePlayerStore(database.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(new PlayerRecord("Kim", 1, 2, 0)));
        Assert.Null(await store.LoadAsync("Kim"));
    }

    [Fact]
    public async Task 종료한_저장소는_새_작업을_거절한다()
    {
        using TempDatabase database = new TempDatabase();
        SqlitePlayerStore store = new SqlitePlayerStore(database.Path);
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadAsync("Kim"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.DrainAsync());
        await store.DisposeAsync();
    }

    [Fact]
    public async Task 초기화가_실패해도_작업은_무한히_기다리지_않고_실패한다()
    {
        using TempDatabase database = new TempDatabase();
        await using SqlitePlayerStore store = new SqlitePlayerStore(System.IO.Path.GetDirectoryName(database.Path)!);
        await Assert.ThrowsAsync<SqliteException>(() => store.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<SqliteException>(() => store.LoadAsync("Kim").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task 여러_생산자의_작업을_처리하고_남은_작업까지_저장한다()
    {
        using TempDatabase database = new TempDatabase();
        await using SqlitePlayerStore store = new SqlitePlayerStore(database.Path);
        Task[] writes = Enumerable.Range(0, 100).AsParallel()
            .Select(index => store.SaveAsync(new PlayerRecord($"user-{index}", index, index))).ToArray();
        await Task.WhenAll(writes);
        await store.DrainAsync();
        for (int index = 0; index < 100; index++)
        {
            Assert.Equal(new PlayerRecord($"user-{index}", index, index), await store.LoadAsync($"user-{index}"));
        }
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyServer-store-tests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "players.db");

        public TempDatabase() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}
