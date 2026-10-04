using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Dapper;
using DragonLord.Api;
using DragonLord.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var runtimeFile=Environment.GetEnvironmentVariable("DRAGON_CONFIG");
if(!string.IsNullOrEmpty(runtimeFile))builder.Configuration.AddJsonFile(runtimeFile,optional:false,reloadOnChange:true);
var connection = builder.Configuration.GetConnectionString("Cafe") ?? throw new InvalidOperationException("ConnectionStrings__Cafe is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.AddSingleton<Store>();
builder.Services.AddSignalR();
if(builder.Environment.IsDevelopment())builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath,".runtime","data-protection"))).SetApplicationName("DragonLord.Local");
builder.Services.AddHttpClient<Razorpay>(c=>c.Timeout=TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("n8n",c=>c.Timeout=TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<AutomationWorker>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o=>{
    o.Cookie.Name="dragon_session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
    o.ExpireTimeSpan=TimeSpan.FromHours(8);o.SlidingExpiration=false;
    o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};
});
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("login",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
builder.Services.AddHostedService<ExpiryWorker>();
builder.Services.AddAuthorization(o => {
    o.AddPolicy("Staff", p => p.RequireAuthenticatedUser().RequireRole("Owner","Manager","Cashier").RequireClaim("venue_id"));
    o.AddPolicy("Manager", p => p.RequireAuthenticatedUser().RequireRole("Owner","Manager").RequireClaim("venue_id"));
    o.AddPolicy("Owner", p => p.RequireAuthenticatedUser().RequireRole("Owner").RequireClaim("venue_id"));
});
DefaultTypeMap.MatchNamesWithUnderscores = true;
var app = builder.Build();
if(args.Contains("--migrate")) {
    await using var db = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
    await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations(name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
    foreach(var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"migrations"),"*.sql").OrderBy(x=>x)) {
        var name=Path.GetFileName(file); await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT pg_advisory_xact_lock(834621)",transaction:tx);
        if(!await db.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name=@name)",new{name},tx)) {
            await db.ExecuteAsync(await File.ReadAllTextAsync(file),transaction:tx);
            await db.ExecuteAsync("INSERT INTO schema_migrations(name) VALUES(@name)",new{name},tx);
        }
        await tx.CommitAsync();
    }
    return;
}
app.Use(async (ctx,next) => { try { await next(); } catch(ArgumentException e) { ctx.Response.StatusCode=400; await ctx.Response.WriteAsJsonAsync(new{error=e.Message}); } catch(PostgresException e) when(e.SqlState is "23P01" or "23505" or "23514" or "40001") { ctx.Response.StatusCode=409; await ctx.Response.WriteAsJsonAsync(new{error="Conflict: reservation, balance or command already changed. Refresh and retry with the same idempotency key."}); } });
// JSON custom-header requirement prevents cross-origin form posts; no permissive CORS.
app.Use(async(ctx,next)=>{if(ctx.Request.Method=="POST"&&ctx.Request.Path.StartsWithSegments("/api")&&!ctx.Request.Path.StartsWithSegments("/api/payments/razorpay/webhook")&&ctx.Request.Headers["X-Dragon-Request"]!="1"){ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="Missing request header."});return;}await next();});
app.UseRateLimiter();app.UseAuthentication(); app.UseAuthorization();
app.Use(async(ctx,next)=>{await next();if(ctx.Request.Method=="POST"&&ctx.Request.Path.StartsWithSegments("/api")&&ctx.Response.StatusCode<400&&ctx.User.FindFirstValue("venue_id") is string venueId){await ctx.RequestServices.GetRequiredService<IHubContext<VenueHub>>().Clients.Group(venueId).SendAsync("Changed",new{kind="workspace"});}});
app.MapAccounts();app.MapWorkspace();
app.MapGet("/health",()=>new{status="ok",mode="reference-api",deviceControl="not-connected"});
app.MapHub<VenueHub>("/hubs/venue").RequireAuthorization("Staff");
var api=app.MapGroup("/api").RequireAuthorization("Staff");
Guid Venue(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue("venue_id")!);
string Actor(ClaimsPrincipal u) => u.FindFirstValue("sub") ?? throw new ArgumentException("Missing staff subject.");
void RequireVenueController() { if(builder.Configuration["Runtime:Mode"]!="VenueController") throw new ArgumentException("Active sessions are owned by the local venue controller. Send this command there."); }
api.MapGet("/customers",(ClaimsPrincipal u,Store db)=>db.Query("SELECT * FROM customers WHERE venue_id=@venue ORDER BY name",new{venue=Venue(u)}));
api.MapGet("/stations",(ClaimsPrincipal u,Store db)=>db.Query("SELECT * FROM stations WHERE venue_id=@venue ORDER BY id",new{venue=Venue(u)}));
api.MapGet("/sessions",(ClaimsPrincipal u,Store db)=>db.Query("SELECT * FROM sessions WHERE venue_id=@venue ORDER BY started_at DESC",new{venue=Venue(u)}));
api.MapGet("/reports/transactions",(ClaimsPrincipal u,Store db,DateTimeOffset from,DateTimeOffset to)=>db.Query("SELECT * FROM ledger WHERE venue_id=@venue AND created_at>=@from AND created_at<@to ORDER BY created_at DESC",new{venue=Venue(u),from,to}));
api.MapGet("/receipts/{id:guid}",async(Guid id,ClaimsPrincipal u,Store db)=>Results.Ok(await db.Query("SELECT l.*,v.name AS venue_name FROM ledger l JOIN venues v ON v.id=l.venue_id WHERE l.venue_id=@venue AND l.id=@id",new{venue=Venue(u),id})));
api.MapPost("/customers",async(CustomerInput input,ClaimsPrincipal u,Store db,HttpRequest request)=> {
    if(string.IsNullOrWhiteSpace(input.Name)||input.Name.Length>120) throw new ArgumentException("Customer name required (max 120 characters).");
    var venue=Venue(u);return await db.Command(venue,Actor(u),request.Headers["Idempotency-Key"].ToString(),"customer",input,async(c,t)=>{
        var id=Guid.NewGuid();await c.ExecuteAsync("INSERT INTO customers(id,venue_id,name,email,member) VALUES(@id,@venue,@Name,@Email,@Member)",new{id,venue,input.Name,input.Email,input.Member},t);return new{id,input.Name};
    });
});
api.MapPost("/payments/cash",async(CashPayment input,ClaimsPrincipal u,Store db)=>{
    if(input.AmountMinor is <=0 or >100000000) throw new ArgumentException("Payment amount must be a positive integer in minor units.");
    var venue=Venue(u);return await db.Command(venue,Actor(u),input.IdempotencyKey,"cash",input,async(c,t)=>{
        await Store.Available(c,t,venue,input.CustomerId);await Store.Post(c,t,venue,input.CustomerId,input.AmountMinor,"Payment",input.IdempotencyKey,Actor(u));return new{input.CustomerId,input.AmountMinor,status="Recorded"};
    });
});
api.MapPost("/sessions",async(StartSession input,ClaimsPrincipal u,Store db,IHubContext<VenueHub> hub)=>{
    RequireVenueController();var venue=Venue(u);
    var result=await db.Command(venue,Actor(u),input.IdempotencyKey,"start",input,async(c,t)=>{
        var station=await c.QuerySingleAsync("SELECT * FROM stations WHERE venue_id=@venue AND id=@station FOR UPDATE",new{venue,station=input.StationId},t);
        if((string)station.state!="Available") throw new ArgumentException("Station unavailable.");
        long rate=(long)station.hourly_minor;var reserve=Billing.Reserve(rate,input.Minutes);var start=DateTimeOffset.UtcNow;var end=start.AddMinutes(input.Minutes);
        if(await Store.Available(c,t,venue,input.CustomerId)<reserve) throw new ArgumentException("Insufficient prepaid balance.");
        if(await c.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM reservation_seats WHERE venue_id=@venue AND station_id=@station AND active AND booking_window && tstzrange(@start,@end,'[)'))",new{venue,station=input.StationId,start,end},t)) throw new ArgumentException("Session overlaps a reservation.");
        var id=Guid.NewGuid();await c.ExecuteAsync("INSERT INTO sessions(id,venue_id,station_id,customer_id,started_at,ends_at,rate_minor,reserved_minor) VALUES(@id,@venue,@StationId,@CustomerId,@start,@end,@rate,@reserve);UPDATE stations SET state='Occupied' WHERE venue_id=@venue AND id=@StationId",new{id,venue,input.StationId,input.CustomerId,start,end,rate,reserve},t);
        return new{id,startedAt=start,endsAt=end,rateMinor=rate,reservedMinor=reserve,deviceControl="not-connected"};
    });await hub.Clients.Group(venue.ToString()).SendAsync("Changed",new{kind="session"});return result;
});
api.MapPost("/sessions/{id:guid}/end",async(Guid id,EndSession input,ClaimsPrincipal u,Store db,IHubContext<VenueHub> hub)=>{
    RequireVenueController();var venue=Venue(u);
    var result=await db.Command(venue,Actor(u),"end:"+id,"end",new{id},async(c,t)=>{
        var session=await c.QuerySingleAsync<SessionRow>("SELECT * FROM sessions WHERE venue_id=@venue AND id=@id FOR UPDATE",new{venue,id},t);
        if(session.EndedAt is not null)return new{id,session.ChargeMinor};
        var end=DateTimeOffset.UtcNow;var billingEnd=session.PausedAt??(end>session.EndsAt?session.EndsAt:end);
        var total=Billing.Charge(session.RateMinor,session.ReservedMinor,session.StartedAt,billingEnd,session.PausedSeconds);
        await Store.Post(c,t,venue,session.CustomerId,-total,"Session","session:"+id,Actor(u));
        await c.ExecuteAsync("UPDATE sessions SET ended_at=@end,charge_minor=@total WHERE id=@id;UPDATE stations SET state='Available' WHERE venue_id=@venue AND id=@station",new{id,venue,end,total,station=session.StationId},t);
        return new{id,chargeMinor=total,endedAt=end,receiptReference="session:"+id};
    });await hub.Clients.Group(venue.ToString()).SendAsync("Changed",new{kind="session"});return result;
});
api.MapPost("/credits",async(CreditInput input,ClaimsPrincipal u,Store db)=>{
    if(input.AmountMinor is <=0 or >10000000 ||string.IsNullOrWhiteSpace(input.Reason))throw new ArgumentException("Positive credit amount and reason required.");
    var venue=Venue(u);return await db.Command(venue,Actor(u),input.IdempotencyKey,"compensation",input,async(c,t)=>{
        await Store.Available(c,t,venue,input.CustomerId);await Store.Post(c,t,venue,input.CustomerId,input.AmountMinor,"Compensation",input.IdempotencyKey+":"+input.Reason,Actor(u));return new{input.CustomerId,input.AmountMinor,input.Reason};
    });
}).RequireAuthorization("Manager");
api.MapPost("/reservations",async(ReservationInput input,ClaimsPrincipal u,Store db)=>{
    RequireVenueController();var venue=Venue(u);
    if(input.Start<DateTimeOffset.UtcNow || input.End<=input.Start || input.StationIds.Length is <1 or >14 || input.StationIds.Distinct().Count()!=input.StationIds.Length)throw new ArgumentException("Invalid reservation window or station selection.");
    return await db.Command(venue,Actor(u),input.IdempotencyKey,"reservation",input,async(c,t)=>{
        await Store.Available(c,t,venue,input.CustomerId);
        foreach(var station in input.StationIds){
            var usable=await c.QuerySingleAsync<bool>("SELECT state NOT IN ('Maintenance','Offline') FROM stations WHERE venue_id=@venue AND id=@station FOR UPDATE",new{venue,station},t);
            if(!usable)throw new ArgumentException("Station unavailable.");
            if(await c.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM sessions WHERE venue_id=@venue AND station_id=@station AND ended_at IS NULL AND started_at<@End AND ends_at>@Start)",new{venue,station,input.Start,input.End},t))throw new ArgumentException("Reservation overlaps an active session.");
        }
        var id=Guid.NewGuid();await c.ExecuteAsync("INSERT INTO reservations(id,venue_id,customer_id,status) VALUES(@id,@venue,@CustomerId,'Confirmed')",new{id,venue,input.CustomerId},t);
        foreach(var station in input.StationIds)await c.ExecuteAsync("INSERT INTO reservation_seats VALUES(@id,@venue,@station,tstzrange(@Start,@End,'[)'),true)",new{id,venue,station,input.Start,input.End},t);
        return new{id,status="Confirmed",acknowledgedBy="VenueController",depositMinor=0};
    });
});
api.MapPost("/orders",async(OrderInput input,ClaimsPrincipal u,Store db)=>{
    if(input.Quantity is <1 or >100)throw new ArgumentException("Quantity must be 1–100.");var venue=Venue(u);
    return await db.Command(venue,Actor(u),input.IdempotencyKey,"order",input,async(c,t)=>{
        var product=await c.QuerySingleAsync("SELECT * FROM products WHERE venue_id=@venue AND id=@ProductId FOR UPDATE",new{venue,input.ProductId},t);
        if((int)product.stock<input.Quantity)throw new ArgumentException("Out of stock.");
        long total=checked((long)product.price_minor*input.Quantity);
        if(await Store.Available(c,t,venue,input.CustomerId)<total)throw new ArgumentException("Insufficient available balance.");
        var id=Guid.NewGuid();await Store.Post(c,t,venue,input.CustomerId,-total,"Purchase","order:"+id,Actor(u));
        await c.ExecuteAsync("UPDATE products SET stock=stock-@Quantity WHERE venue_id=@venue AND id=@ProductId;INSERT INTO orders(id,venue_id,customer_id,station_id,product_id,quantity,total_minor) VALUES(@id,@venue,@CustomerId,@StationId,@ProductId,@Quantity,@total)",new{id,venue,input.CustomerId,input.StationId,input.ProductId,input.Quantity,total},t);
        return new{id,totalMinor=total,status="Pending"};
    });
});
api.MapGet("/integrations",(Razorpay razor,IConfiguration config)=>new{razorpay=razor.Configured?"Configured":"Needs keys",n8n=string.IsNullOrEmpty(config["N8n:WebhookUrl"])?"Needs setup":"Configured"});
api.MapPost("/payments/razorpay/orders",(RazorpayOrderInput input,ClaimsPrincipal u,Razorpay razor)=>razor.CreateOrder(Venue(u),input.CustomerId,input.AmountMinor,input.IntentId,Actor(u)));
api.MapPost("/payments/razorpay/verify",(RazorpayVerifyInput input,ClaimsPrincipal u,Razorpay razor)=>razor.VerifyCheckout(Venue(u),input.IntentId,input.PaymentId,input.Signature));
app.MapPost("/api/payments/razorpay/webhook",async(HttpRequest request,Razorpay razor)=>{
    if(!razor.Configured)return Results.Problem("Razorpay is not configured.",statusCode:503);
    if(request.ContentLength>1048576)return Results.StatusCode(413);
    using var buffer=new MemoryStream();var block=new byte[8192];int read;
    while((read=await request.Body.ReadAsync(block))>0){if(buffer.Length+read>1048576)return Results.StatusCode(413);await buffer.WriteAsync(block.AsMemory(0,read));}
    return Results.Ok(await razor.Webhook(buffer.ToArray(),request.Headers["X-Razorpay-Signature"].ToString(),request.Headers["X-Razorpay-Event-Id"].ToString()));
});
api.MapGet("/automations",(ClaimsPrincipal u,Store db)=>db.Query("SELECT o.id,o.kind,o.created_at,d.attempts,d.delivered_at,d.last_error FROM outbox o LEFT JOIN automation_deliveries d ON d.event_id=o.id WHERE o.venue_id=@venue ORDER BY o.created_at DESC LIMIT 100",new{venue=Venue(u)})).RequireAuthorization("Manager");
api.MapGet("/handover",(ClaimsPrincipal u,Store db)=>db.Query("SELECT * FROM handover_notes WHERE venue_id=@venue ORDER BY created_at DESC",new{venue=Venue(u)}));
app.Run();
public sealed class VenueHub : Hub
{
    public override async Task OnConnectedAsync() { var venue=Context.User?.FindFirstValue("venue_id");if(venue is null){Context.Abort();return;}await Groups.AddToGroupAsync(Context.ConnectionId,venue);await base.OnConnectedAsync(); }
}
