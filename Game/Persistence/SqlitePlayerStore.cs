using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Game.Persistence;

public sealed class SqlitePlayerStore : IPlayerStore
{
    private sealed record WorkItem(Action<SqliteConnection> Run, Action<Exception> Fail);
    private readonly Channel<WorkItem> _jobs = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly Task _worker;
    private int _disposed;

    public SqlitePlayerStore(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        _worker = Task.Run(() => ProcessAsync(connectionString));
    }

    public Task<PlayerRecord?> LoadAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Enqueue<PlayerRecord?>(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name, x, y, room_id FROM players WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read()
                ? new PlayerRecord(reader.GetString(0), (float)reader.GetDouble(1), (float)reader.GetDouble(2), reader.GetInt32(3))
                : null;
        });
    }

    public Task SaveAsync(PlayerRecord player)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentException.ThrowIfNullOrWhiteSpace(player.Name);
        if (!float.IsFinite(player.X) || !float.IsFinite(player.Y) ||
            player.X < 0 || player.X > 100 || player.Y < 0 || player.Y > 100 || player.RoomId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(player));
        }

        return Enqueue(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO players(name, x, y, room_id) VALUES($name, $x, $y, $room)
                ON CONFLICT(name) DO UPDATE SET x = excluded.x, y = excluded.y, room_id = excluded.room_id
                """;
            command.Parameters.AddWithValue("$name", player.Name);
            command.Parameters.AddWithValue("$x", player.X);
            command.Parameters.AddWithValue("$y", player.Y);
            command.Parameters.AddWithValue("$room", player.RoomId);
            command.ExecuteNonQuery();
            return true;
        });
    }

    public Task DrainAsync() => Enqueue(_ => true);

    private Task<T> Enqueue<T>(Func<SqliteConnection, T> operation)
    {
        TaskCompletionSource<T> result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkItem item = new WorkItem(connection => result.TrySetResult(operation(connection)), error => result.TrySetException(error));
        if (Volatile.Read(ref _disposed) != 0 || !_jobs.Writer.TryWrite(item))
        {
            result.TrySetException(new ObjectDisposedException(nameof(SqlitePlayerStore)));
        }
        return result.Task;
    }

    private async Task ProcessAsync(string connectionString)
    {
        using SqliteConnection connection = new SqliteConnection(connectionString);
        Exception? initializationError = null;
        try
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL";
            command.ExecuteNonQuery();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS players(
                    name TEXT PRIMARY KEY COLLATE BINARY,
                    x REAL NOT NULL CHECK(x >= 0 AND x <= 100),
                    y REAL NOT NULL CHECK(y >= 0 AND y <= 100),
                    room_id INTEGER NOT NULL CHECK(room_id >= 1)
                )
                """;
            command.ExecuteNonQuery();
        }
        catch (Exception error)
        {
            initializationError = error;
        }

        await foreach (WorkItem item in _jobs.Reader.ReadAllAsync())
        {
            if (initializationError != null)
            {
                item.Fail(initializationError);
                continue;
            }
            try { item.Run(connection); }
            catch (Exception error) { item.Fail(error); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _jobs.Writer.TryComplete();
        }
        await _worker;
    }
}
