using Dapper;
using DragonLord.Domain;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
namespace DragonLord.Api;
public sealed class ExpiryWorker(NpgsqlDataSource source,Store store,IConfiguration config,IHubContext<VenueHub> hub,ILogger<ExpiryWorker> log):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stop){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));while(await timer.WaitForNextTickAsync(stop)){if(config["Runtime:Mode"]!="VenueController")continue;try{await using var c=await source.OpenConnectionAsync(stop);var rows=await c.QueryAsync("SELECT id,venue_id FROM sessions WHERE ended_at IS NULL AND paused_at IS NULL AND ends_at<=now()");foreach(var row in rows){Guid id=row.id;Guid venue=row.venue_id;await store.Command(venue,"Local controller","end:"+id,"end",new{id},async(db,tx)=>{var s=await db.QuerySingleAsync<SessionRow>("SELECT * FROM sessions WHERE id=@id FOR UPDATE",new{id},tx);if(s.EndedAt is not null)return new{id,s.ChargeMinor};var total=Billing.Charge(s.RateMinor,s.ReservedMinor,s.StartedAt,s.EndsAt,s.PausedSeconds);await Store.Post(db,tx,venue,s.CustomerId,-total,"Session","session:"+id,"Local controller");await db.ExecuteAsync("UPDATE sessions SET ended_at=ends_at,charge_minor=@total WHERE id=@id;UPDATE stations SET state='Available' WHERE venue_id=@venue AND id=@station",new{id,venue,total,station=s.StationId},tx);return new{id,chargeMinor=total,endedAt=s.EndsAt};});await hub.Clients.Group(venue.ToString()).SendAsync("Changed",new{kind="session.expired"},stop);}}catch(Exception ex)when(!stop.IsCancellationRequested){log.LogWarning("Expiry retry: {ErrorType}",ex.GetType().Name);}}}
}
