using Dapper;
using Npgsql;
namespace DragonLord.Api;

public static class DatabaseMigrator
{
    public static async Task Apply(NpgsqlDataSource source)
    {
        await using var database = await source.OpenConnectionAsync();
        await database.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations(name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
        var folder = Path.Combine(AppContext.BaseDirectory, "migrations");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Database migration directory is missing: {folder}");
        foreach (var file in Directory.GetFiles(folder, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            await using var transaction = await database.BeginTransactionAsync();
            await database.ExecuteAsync("SELECT pg_advisory_xact_lock(834621)", transaction: transaction);
            var applied = await database.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name=@name)", new { name }, transaction);
            if (!applied)
            {
                await database.ExecuteAsync(await File.ReadAllTextAsync(file), transaction: transaction);
                await database.ExecuteAsync("INSERT INTO schema_migrations(name) VALUES(@name)", new { name }, transaction);
            }
            await transaction.CommitAsync();
        }
    }
}
