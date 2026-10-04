using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
var builder=Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ControllerSimulator>();
await builder.Build().RunAsync();
// Local restart-safe expiry/outbox simulator. Does not accept unauthenticated network grants.
sealed class ControllerSimulator(ILogger<ControllerSimulator> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var path=Environment.GetEnvironmentVariable("DRAGON_CONTROLLER_DB")??"controller-simulator.db";
        await using var db=new SqliteConnection($"Data Source={path}");await db.OpenAsync(stop);
        var setup=db.CreateCommand();setup.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS grants(id TEXT PRIMARY KEY, station TEXT NOT NULL, expires_at TEXT NOT NULL, reserved_minor INTEGER NOT NULL CHECK(reserved_minor>0), ended INTEGER NOT NULL DEFAULT 0); CREATE TABLE IF NOT EXISTS outbox(id TEXT PRIMARY KEY, payload TEXT NOT NULL, acknowledged INTEGER NOT NULL DEFAULT 0);";await setup.ExecuteNonQueryAsync(stop);
        log.LogWarning("CONTROLLER SIMULATOR: grants must be imported through a future authenticated integration. Cloud transport and device locking are not connected.");
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        while(await timer.WaitForNextTickAsync(stop))
        {
            using var tx=db.BeginTransaction();var read=db.CreateCommand();read.Transaction=tx;read.CommandText="SELECT id,station,expires_at FROM grants WHERE ended=0 AND julianday(expires_at)<=julianday('now')";
            var expired=new List<(string Id,string Station,string End)>();await using(var r=await read.ExecuteReaderAsync(stop)){while(await r.ReadAsync(stop))expired.Add((r.GetString(0),r.GetString(1),r.GetString(2)));}
            foreach(var g in expired){var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE grants SET ended=1 WHERE id=$id; INSERT OR IGNORE INTO outbox(id,payload) VALUES($event,$payload)";cmd.Parameters.AddWithValue("$id",g.Id);cmd.Parameters.AddWithValue("$event","end:"+g.Id);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(new{sessionId=g.Id,stationId=g.Station,endedAt=g.End,reason="PrepaidLimitExpired"}));await cmd.ExecuteNonQueryAsync(stop);log.LogInformation("Simulated prepaid limit ended for {Station}",g.Station);}
            await tx.CommitAsync(stop);
        }
    }
}
