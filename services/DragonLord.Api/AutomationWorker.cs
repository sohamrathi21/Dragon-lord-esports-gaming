using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
namespace DragonLord.Api;
// At-least-once event delivery. Receiver must deduplicate by eventId before side effects.
public sealed class AutomationWorker(NpgsqlDataSource source,IConfiguration config,IHttpClientFactory factory,ILogger<AutomationWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
        while(await timer.WaitForNextTickAsync(stop))
        {
            var url=config["N8n:WebhookUrl"];var secret=config["N8n:HeaderSecret"];
            if(string.IsNullOrEmpty(url)||string.IsNullOrEmpty(secret))continue;
            if(!Uri.TryCreate(url,UriKind.Absolute,out var destination)||(destination.Scheme!="https"&&!destination.IsLoopback)){log.LogError("n8n URL must use HTTPS outside loopback.");continue;}
            try{
                await using var db=await source.OpenConnectionAsync(stop);
                await db.ExecuteAsync("INSERT INTO automation_deliveries(event_id) SELECT id FROM outbox ON CONFLICT DO NOTHING");
                await using var tx=await db.BeginTransactionAsync(stop);
                var batch=await db.QueryAsync<EventRow>("SELECT o.id,o.venue_id,o.kind,o.payload::text,d.attempts FROM outbox o JOIN automation_deliveries d ON d.event_id=o.id WHERE d.delivered_at IS NULL AND d.next_attempt_at<=now() AND d.attempts<10 ORDER BY o.created_at LIMIT 10 FOR UPDATE OF d SKIP LOCKED",transaction:tx);
                foreach(var e in batch){
                    var body=JsonSerializer.Serialize(new{eventId=e.Id,venueId=e.VenueId,type=e.Kind,data=JsonSerializer.Deserialize<JsonElement>(e.Payload)});
                    using var request=new HttpRequestMessage(HttpMethod.Post,destination){Content=new StringContent(body,Encoding.UTF8,"application/json")};request.Headers.Add("X-Dragon-Token",secret);request.Headers.Add("Idempotency-Key",e.Id.ToString());
                    try{
                        using var response=await factory.CreateClient("n8n").SendAsync(request,stop);
                        if(!response.IsSuccessStatusCode)throw new HttpRequestException("n8n returned HTTP "+(int)response.StatusCode);
                        await db.ExecuteAsync("UPDATE automation_deliveries SET delivered_at=now(),attempts=attempts+1,last_error=NULL WHERE event_id=@Id",e,tx);
                    }catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException){
                        await db.ExecuteAsync("UPDATE automation_deliveries SET attempts=attempts+1,last_error=@error,next_attempt_at=now()+(@delay * interval '1 second') WHERE event_id=@id",new{id=e.Id,error=ex.GetType().Name,delay=Math.Min(3600,Math.Pow(2,e.Attempts)*15)},tx);
                    }
                }
                await tx.CommitAsync(stop);
            }catch(Exception ex) when(!stop.IsCancellationRequested){log.LogWarning("Automation delivery deferred: {ErrorType}",ex.GetType().Name);}
        }
    }
    private sealed class EventRow {public Guid Id{get;set;}public Guid VenueId{get;set;}public string Kind{get;set;}="";public string Payload{get;set;}="{}";public int Attempts{get;set;}}
}
