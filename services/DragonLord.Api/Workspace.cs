using System.Security.Claims;
using System.Text.Json;
using Dapper;
using DragonLord.Domain;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
namespace DragonLord.Api;
public static class Workspace
{
 static long Epoch(object value)=>value is DateTimeOffset offset?offset.ToUnixTimeMilliseconds():new DateTimeOffset((DateTime)value).ToUnixTimeMilliseconds();
 static Guid Venue(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue("venue_id")!);
 public static void MapWorkspace(this WebApplication app)
 {
  app.MapGet("/api/workspace",async(ClaimsPrincipal user,NpgsqlDataSource source)=>{
   var venue=Venue(user);await using var c=await source.OpenConnectionAsync();var args=new{venue};
   var v=await c.QuerySingleAsync("SELECT * FROM venues WHERE id=@venue",args);
   var stations=(await c.QueryAsync("SELECT s.*,EXISTS(SELECT 1 FROM devices d WHERE d.venue_id=s.venue_id AND d.station_id=s.id AND NOT revoked AND last_seen_at>now()-interval '30 seconds') AS connected FROM stations s WHERE venue_id=@venue ORDER BY id",args)).Select(x=>new{id=(string)x.id,zone=(string)x.zone,status=(string)x.state,game=(string)x.games,online=(bool)x.connected}).ToArray();
   var customers=(await c.QueryAsync("SELECT * FROM customers WHERE venue_id=@venue ORDER BY name",args)).Select(x=>new{id=(Guid)x.id,name=(string)x.name,email=(string?)x.email??"",balance=(long)x.balance_minor,member=(bool)x.member}).ToArray();
   var sessions=(await c.QueryAsync<SessionRow>("SELECT * FROM sessions WHERE venue_id=@venue ORDER BY started_at DESC",args)).Select(x=>new{id=x.Id,stationId=x.StationId,customerId=x.CustomerId,started=x.StartedAt.ToUnixTimeMilliseconds(),ends=x.EndsAt.ToUnixTimeMilliseconds(),minutes=(int)(x.EndsAt-x.StartedAt).TotalMinutes,rate=x.RateMinor,reserved=x.ReservedMinor,ended=x.EndedAt?.ToUnixTimeMilliseconds(),charge=x.ChargeMinor,pausedAt=x.PausedAt?.ToUnixTimeMilliseconds(),pausedMs=x.PausedSeconds*1000}).ToArray();
   var ledger=(await c.QueryAsync("SELECT * FROM ledger WHERE venue_id=@venue ORDER BY created_at DESC LIMIT 1000",args)).Select(x=>new{id=(Guid)x.id,key=(string)x.reference,customerId=(Guid)x.customer_id,kind=(string)x.kind,amount=(long)x.amount_minor,at=Epoch(x.created_at),note=(string)x.reference}).ToArray();
   var bookings=(await c.QueryAsync("SELECT r.*,array_agg(s.station_id) AS seats,min(lower(s.booking_window)) AS starts,max(upper(s.booking_window)) AS ends FROM reservations r JOIN reservation_seats s ON s.reservation_id=r.id WHERE r.venue_id=@venue GROUP BY r.id",args)).Select(x=>new{id=(Guid)x.id,customerId=(Guid)x.customer_id,stations=(string[])x.seats,start=Epoch(x.starts),end=Epoch(x.ends),status=(string)x.status,deposit=(long)x.deposit_minor}).ToArray();
   var products=(await c.QueryAsync("SELECT * FROM products WHERE venue_id=@venue ORDER BY name",args)).Select(x=>new{id=(Guid)x.id,name=(string)x.name,category=(string)x.category,price=(long)x.price_minor,stock=(int)x.stock,emoji=(string)x.emoji}).ToArray();
   var orders=(await c.QueryAsync("SELECT * FROM orders WHERE venue_id=@venue ORDER BY created_at DESC",args)).Select(x=>new{id=(Guid)x.id,stationId=(string)x.station_id,customerId=(Guid)x.customer_id,items=new[]{new{productId=(Guid)x.product_id,qty=(int)x.quantity}},total=(long)x.total_minor,status=(string)x.status,at=Epoch(x.created_at)}).ToArray();
   var audit=(await c.QueryAsync("SELECT * FROM audit WHERE venue_id=@venue ORDER BY id DESC LIMIT 100",args)).Select(x=>new{at=Epoch(x.created_at),actor=(string)x.actor,action=(string)x.action}).ToArray();
   var waitlist=(await c.QueryAsync("SELECT * FROM waitlist WHERE venue_id=@venue",args)).Select(x=>new{id=(Guid)x.id,name=(string)x.name,size=(int)x.size}).ToArray();
   var rateRows=await c.QueryAsync("SELECT zone,max(hourly_minor) AS rate FROM stations WHERE venue_id=@venue GROUP BY zone",args);var rates=rateRows.ToDictionary(x=>(string)x.zone,x=>(long)x.rate);
   return Results.Ok(new{stations,customers,sessions,ledger,bookings,products,orders,audit,waitlist,brand=new{name=(string)v.name,domain=(string)v.domain,logo=(string)v.logo_url},rates,warning=(int)v.warning_minutes,online=true});
  }).RequireAuthorization("Staff");
  app.MapPost("/api/actions/{action}",async(string action,JsonElement data,ClaimsPrincipal user,Store store,IHubContext<VenueHub> hub,HttpRequest request,IConfiguration config)=>{
   var venue=Venue(user);var actor=user.FindFirstValue("sub")!;
   if(new[]{"refund","compensation","stock","maintenance","product","incidentReview"}.Contains(action)&&!user.IsInRole("Owner")&&!user.IsInRole("Manager"))return Results.StatusCode(403);
   if(action=="settings"&&!user.IsInRole("Owner"))return Results.StatusCode(403);
   string S(string key)=>data.GetProperty(key).GetString()??"";int I(string key)=>data.GetProperty(key).GetInt32();long L(string key)=>data.GetProperty(key).GetInt64();Guid G(string key)=>Guid.Parse(S(key));
   if(new[]{"extend","transfer","pause","resume"}.Contains(action)&&config["Runtime:Mode"]!="VenueController")throw new ArgumentException("Session commands require the local venue controller.");
   var result=await store.Command(venue,actor,request.Headers["Idempotency-Key"].ToString(),action,data,async(c,t)=>{
    if(new[]{"extend","transfer","pause","resume"}.Contains(action)){
     var id=G("sessionId");var x=await c.QuerySingleAsync<SessionRow>("SELECT * FROM sessions WHERE venue_id=@venue AND id=@id FOR UPDATE",new{venue,id},t);if(x.EndedAt is not null)throw new ArgumentException("Session already ended.");var now=DateTimeOffset.UtcNow;
     var end=x.EndsAt;var station=x.StationId;
     if(action=="pause"){if(x.PausedAt is not null)throw new ArgumentException("Already paused.");await c.ExecuteAsync("UPDATE sessions SET paused_at=@now WHERE id=@id",new{id,now},t);}
     if(action=="resume"){if(x.PausedAt is null)throw new ArgumentException("Session is not paused.");var seconds=(long)(now-x.PausedAt.Value).TotalSeconds;end=end.AddSeconds(seconds);await CheckReservation(c,t,venue,station,now,end);await c.ExecuteAsync("UPDATE sessions SET paused_at=NULL,paused_seconds=paused_seconds+@seconds,ends_at=@end WHERE id=@id",new{id,seconds,end},t);}
     if(action=="extend"){var extra=Billing.Reserve(x.RateMinor,I("minutes"));if(await Store.Available(c,t,venue,x.CustomerId)<extra)throw new ArgumentException("Insufficient balance.");end=end.AddMinutes(I("minutes"));await CheckReservation(c,t,venue,station,now,end);await c.ExecuteAsync("UPDATE sessions SET ends_at=@end,reserved_minor=reserved_minor+@extra WHERE id=@id",new{id,end,extra},t);}
     if(action=="transfer"){station=S("stationId");var state=await c.QuerySingleAsync<string>("SELECT state FROM stations WHERE venue_id=@venue AND id=@station FOR UPDATE",new{venue,station},t);if(state!="Available")throw new ArgumentException("Target station unavailable.");await CheckReservation(c,t,venue,station,now,end);await c.ExecuteAsync("UPDATE stations SET state='Available' WHERE venue_id=@venue AND id=@old;UPDATE stations SET state='Occupied' WHERE venue_id=@venue AND id=@station;UPDATE sessions SET station_id=@station WHERE id=@id",new{venue,id,station,old=x.StationId},t);}
    }
    else if(action=="refund"||action=="compensation"){
     var customer=G("customerId");var amount=L("amount");var reason=S("reason");if(amount<=0||amount>10000000||string.IsNullOrWhiteSpace(reason))throw new ArgumentException("Positive amount and reason required.");var available=await Store.Available(c,t,venue,customer);if(action=="refund"&&available<amount)throw new ArgumentException("Refund exceeds available balance.");await Store.Post(c,t,venue,customer,action=="refund"?-amount:amount,action=="refund"?"Refund":"Compensation",request.Headers["Idempotency-Key"]+":"+reason,actor);
    }
    else if(action=="maintenance"){var station=S("stationId");var state=await c.QuerySingleAsync<string>("SELECT state FROM stations WHERE venue_id=@venue AND id=@station FOR UPDATE",new{venue,station},t);if(state=="Occupied")throw new ArgumentException("End or transfer the session first.");await c.ExecuteAsync("UPDATE stations SET state=@state WHERE venue_id=@venue AND id=@station",new{venue,station,state=state=="Maintenance"?"Available":"Maintenance"},t);}
    else if(action=="cancelBooking"){var id=G("id");var status=data.TryGetProperty("noShow",out var show)&&show.GetBoolean()?"NoShow":"Cancelled";await c.ExecuteAsync("UPDATE reservations SET status=@status WHERE venue_id=@venue AND id=@id;UPDATE reservation_seats SET active=false WHERE venue_id=@venue AND reservation_id=@id",new{venue,id,status},t);}
    else if(action=="orderStatus"){
     var id=G("id");var status=S("status");if(!new[]{"Preparing","Delivered","Cancelled"}.Contains(status))throw new ArgumentException("Invalid order status.");var order=await c.QuerySingleAsync("SELECT * FROM orders WHERE venue_id=@venue AND id=@id FOR UPDATE",new{venue,id},t);if(new[]{"Cancelled","Delivered"}.Contains((string)order.status))throw new ArgumentException("Order already closed.");
     if(status=="Cancelled"){await Store.Post(c,t,venue,(Guid)order.customer_id,(long)order.total_minor,"Refund","cancel:"+id,actor);await c.ExecuteAsync("UPDATE products SET stock=stock+@quantity WHERE venue_id=@venue AND id=@product",new{venue,quantity=(int)order.quantity,product=(Guid)order.product_id},t);}await c.ExecuteAsync("UPDATE orders SET status=@status WHERE id=@id",new{id,status},t);
    }
    else if(action=="stock"){var id=G("id");var qty=I("qty");if(string.IsNullOrWhiteSpace(S("reason")))throw new ArgumentException("Reason required.");await c.ExecuteAsync("UPDATE products SET stock=stock+@qty WHERE venue_id=@venue AND id=@id",new{venue,id,qty},t);}
    else if(action=="product"){if(L("price")<=0||I("stock")<0||string.IsNullOrWhiteSpace(S("name")))throw new ArgumentException("Enter a name, positive price and non-negative stock.");await c.ExecuteAsync("INSERT INTO products(id,venue_id,name,price_minor,stock,category,emoji) VALUES(@id,@venue,@name,@price,@stock,@category,'☕')",new{id=Guid.NewGuid(),venue,name=S("name"),price=L("price"),stock=I("stock"),category=S("category")},t);}
    else if(action=="settings"){
     var brand=data.GetProperty("brand");var rates=data.GetProperty("rates");var warning=I("warning");if(warning is <1 or >60)throw new ArgumentException("Invalid warning threshold.");
     await c.ExecuteAsync("UPDATE venues SET name=@name,domain=@domain,logo_url=@logo,warning_minutes=@warning WHERE id=@venue",new{venue,name=brand.GetProperty("name").GetString(),domain=brand.GetProperty("domain").GetString(),logo=brand.GetProperty("logo").GetString(),warning},t);
     foreach(var zone in new[]{"Standard","VIP","Console"}){var rate=rates.GetProperty(zone).GetInt64();if(rate<=0)throw new ArgumentException("Rates must be positive.");await c.ExecuteAsync("UPDATE stations SET hourly_minor=@rate WHERE venue_id=@venue AND zone=@zone",new{venue,zone,rate},t);}
    }
    else if(action=="waitlist")await c.ExecuteAsync("INSERT INTO waitlist VALUES(@id,@venue,@name,@size)",new{id=Guid.NewGuid(),venue,name=S("name"),size=I("size")},t);
    else if(action=="removeWaitlist")await c.ExecuteAsync("DELETE FROM waitlist WHERE venue_id=@venue AND id=@id",new{venue,id=G("id")},t);
    else if(action=="handover")await c.ExecuteAsync("INSERT INTO handover_notes(id,venue_id,body,priority,actor) VALUES(@id,@venue,@body,@priority,@actor)",new{id=Guid.NewGuid(),venue,body=S("body"),priority=S("priority"),actor},t);
    else if(action=="resolveNote")await c.ExecuteAsync("UPDATE handover_notes SET resolved=true WHERE venue_id=@venue AND id=@id",new{venue,id=G("id")},t);
    else if(action=="incident")await c.ExecuteAsync("INSERT INTO incidents(id,venue_id,station_id,title,minutes_lost,actor) VALUES(@id,@venue,@station,@title,@minutes,@actor)",new{id=Guid.NewGuid(),venue,station=S("station"),title=S("title"),minutes=I("minutes"),actor},t);
    else if(action=="incidentReview")await c.ExecuteAsync("UPDATE incidents SET status='Reviewed' WHERE venue_id=@venue AND id=@id",new{venue,id=G("id")},t);
    else if(action=="shift"){
     var counted=L("counted");if(counted<0)throw new ArgumentException("Invalid counted cash.");var from=DateTimeOffset.FromUnixTimeMilliseconds(L("from"));var expected=await c.QuerySingleAsync<long>("SELECT COALESCE(SUM(amount_minor),0)::bigint FROM ledger WHERE venue_id=@venue AND created_at>=@from AND actor=@actor AND ((kind='Payment' AND reference NOT LIKE 'razorpay:%') OR (kind='Refund' AND amount_minor<0))",new{venue,from,actor},t);await c.ExecuteAsync("INSERT INTO shifts(id,venue_id,actor,opened_at,closed_at,expected_minor,counted_minor,reason) VALUES(@id,@venue,@actor,@from,now(),@expected,@counted,@reason)",new{id=Guid.NewGuid(),venue,actor,from,expected,counted,reason=data.TryGetProperty("reason",out var r)?r.GetString():"Shift reconciliation"},t);
    }
    else throw new ArgumentException("This action is unavailable in live mode.");
    return new{action,status="Saved"};
   });await hub.Clients.Group(venue.ToString()).SendAsync("Changed",new{kind=action});return Results.Ok(result);
  }).RequireAuthorization("Staff");
  app.MapGet("/api/incidents",(ClaimsPrincipal u,Store db)=>db.Query("SELECT * FROM incidents WHERE venue_id=@venue ORDER BY created_at DESC",new{venue=Venue(u)})).RequireAuthorization("Staff");
 }
 static async Task CheckReservation(NpgsqlConnection c,NpgsqlTransaction t,Guid venue,string station,DateTimeOffset start,DateTimeOffset end){if(await c.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM reservation_seats WHERE venue_id=@venue AND station_id=@station AND active AND booking_window && tstzrange(@start,@end,'[)'))",new{venue,station,start,end},t))throw new ArgumentException("The action overlaps a reservation.");}
}
