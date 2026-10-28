using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace SecretSanta.Data;

// One process owns this single-event database; serialize operations, including unlock and redraw.
public sealed class DatabaseVault
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly string path;
    private string? connectionString;
    public bool IsUnlocked => Volatile.Read(ref connectionString) is not null;
    public bool IsInitialized => File.Exists(path);

    public DatabaseVault(IConfiguration config, IHostEnvironment environment)
    {
        Batteries_V2.Init();
        var directory = Path.GetFullPath(config["Database:Directory"] ?? "App_Data", environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "santa.db");
    }

    public async Task UnlockAsync(string password, bool initialize)
    {
        await mutex.WaitAsync();

        try
        {
            if (IsUnlocked)
                throw new InvalidOperationException("The database is already unlocked.");

            if (initialize == IsInitialized)
                throw new InvalidOperationException(initialize ? "A database already exists. Unlock it instead." : "Initialize the database first.");

            if (string.IsNullOrEmpty(password) || password.Length > 1024)
                throw new InvalidOperationException("Enter a valid database password.");

            var target = initialize ? path + ".initializing" : path;
            if (initialize && File.Exists(target))
                File.Delete(target);

            var candidate = new SqliteConnectionStringBuilder
            {
                DataSource = target, Password = password, Pooling = false,
                Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite
            }.ToString();

            try
            {
                await using (var db = Create(candidate))
                {
                    await db.Database.OpenConnectionAsync();
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "PRAGMA cipher_version;";

                    if (string.IsNullOrWhiteSpace(Convert.ToString(await command.ExecuteScalarAsync())))
                        throw new InvalidOperationException("SQLCipher is unavailable. Refusing to open an unencrypted database.");

                    if (initialize)
                    {
                        await db.Database.EnsureCreatedAsync();
                        db.Cycles.Add(new SantaCycle());
                        await db.SaveChangesAsync();
                        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
                    }
                    else if (!await db.Cycles.AnyAsync(c => c.Id == 1))
                    {
                        throw new InvalidOperationException("This is not an initialized Secret Santa database.");
                    }
                }

                if (initialize)
                    File.Move(target, path);

                connectionString = new SqliteConnectionStringBuilder(candidate) { DataSource = path }.ToString();
            }
            catch (SqliteException)
            {
                await Task.Delay(1500);

                throw new InvalidOperationException("Could not unlock the database. Check the password and database file.");
            }
            finally
            {
                if (initialize)
                    foreach (var suffix in new[] { "", "-wal", "-shm" })
                        if (File.Exists(target + suffix))
                            File.Delete(target + suffix);
            }
        }
        finally
        {
            mutex.Release();
        }
    }

    public async Task<T> UseAsync<T>(Func<SantaDb, Task<T>> action)
    {
        await mutex.WaitAsync();

        try
        {
            if (connectionString is null)
                throw new InvalidOperationException("The event is unavailable until an admin unlocks the database.");

            await using var db = Create(connectionString);

            return await action(db);
        }
        finally
        {
            mutex.Release();
        }
    }

    private static SantaDb Create(string connection) => new(new DbContextOptionsBuilder<SantaDb>().UseSqlite(connection).Options);
}
