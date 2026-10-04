using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace DragonLord.Api;

public sealed class Store(NpgsqlDataSource source)
{
    public async Task<object> Query(string sql, object args)
    {
        await using var db = await source.OpenConnectionAsync();
        return (await db.QueryAsync(sql, args)).ToArray();
    }
    public async Task<T> ReadOne<T>(string sql, object args)
    {
        await using var db = await source.OpenConnectionAsync();
        return await db.QuerySingleAsync<T>(sql, args);
    }
    // Venue-scoped serializable transaction and durable idempotent response.
    // All wallet, reservation and session writers acquire this lock, including the expiry worker.
    public async Task<JsonElement> Command(Guid venue, string actor, string key, string kind, object request, Func<NpgsqlConnection,NpgsqlTransaction,Task<object>> action)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 120) throw new ArgumentException("Idempotency key required (maximum 120 characters).");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + JsonSerializer.Serialize(request))));
        await using var db = await source.OpenConnectionAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await db.ExecuteAsync("SELECT id FROM venues WHERE id=@venue FOR UPDATE", new {venue}, tx);
        var previous = await db.QuerySingleOrDefaultAsync<Saved>("SELECT fingerprint, response::text FROM commands WHERE venue_id=@venue AND key=@key", new {venue,key},tx);
        if (previous is not null)
        {
            if(previous.Fingerprint!=fingerprint) throw new ArgumentException("Idempotency key was reused with a different request.");
            return JsonSerializer.Deserialize<JsonElement>(previous.Response);
        }
        var result = await action(db,tx);
        var json = JsonSerializer.Serialize(result);
        await db.ExecuteAsync("INSERT INTO commands VALUES(@venue,@key,@fingerprint,CAST(@json AS jsonb))",new {venue,key,fingerprint,json},tx);
        await db.ExecuteAsync("INSERT INTO audit(venue_id,actor,action,detail) VALUES(@venue,@actor,@kind,CAST(@json AS jsonb))",new {venue,actor,kind,json},tx);
        await db.ExecuteAsync("INSERT INTO outbox(id,venue_id,kind,payload) VALUES(@id,@venue,@kind,CAST(@json AS jsonb))",new {id=Guid.NewGuid(),venue,kind,json},tx);
        await tx.CommitAsync();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
    public static async Task<long> Available(NpgsqlConnection db,NpgsqlTransaction tx,Guid venue,Guid customer)
    {
        var balance = await db.QuerySingleAsync<long>("SELECT balance_minor FROM customers WHERE venue_id=@venue AND id=@customer FOR UPDATE",new {venue,customer},tx);
        var held = await db.QuerySingleAsync<long>("SELECT COALESCE(SUM(reserved_minor),0)::bigint FROM sessions WHERE venue_id=@venue AND customer_id=@customer AND ended_at IS NULL",new {venue,customer},tx);
        return balance-held;
    }
    public static async Task Post(NpgsqlConnection db,NpgsqlTransaction tx,Guid venue,Guid customer,long amount,string kind,string reference,string actor)
    {
        await db.ExecuteAsync("UPDATE customers SET balance_minor=balance_minor+@amount WHERE id=@customer AND venue_id=@venue",new {venue,customer,amount},tx);
        await db.ExecuteAsync("INSERT INTO ledger VALUES(@id,@venue,@customer,@kind,@amount,@reference,@actor,now())",new {id=Guid.NewGuid(),venue,customer,kind,amount,reference,actor},tx);
    }
    private sealed record Saved(string Fingerprint,string Response);
}
public sealed class SessionRow
{
    public Guid Id {get;set;}
    public Guid CustomerId {get;set;}
    public string StationId {get;set;} = "";
    public DateTimeOffset StartedAt {get;set;}
    public DateTimeOffset EndsAt {get;set;}
    public DateTimeOffset? EndedAt {get;set;}
    public DateTimeOffset? PausedAt {get;set;}
    public long PausedSeconds {get;set;}
    public long RateMinor {get;set;}
    public long ReservedMinor {get;set;}
    public long? ChargeMinor {get;set;}
}
