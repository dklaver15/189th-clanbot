using ClanGuardBot.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Tests;

/// <summary>
/// The bot's real schema on an in-memory SQLite database that lives as long as
/// this object (the connection stays open). Relational, so ExecuteUpdate works.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    /// <summary>A fresh context on the shared database, like each DI scope in the bot.</summary>
    public BotDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
