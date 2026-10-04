using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Npgsql;
namespace DragonLord.Api;
public static class Accounts
{
    public static void MapAccounts(this WebApplication app)
    {
        app.MapGet("/api/auth/status",async(NpgsqlDataSource source)=>{await using var c=await source.OpenConnectionAsync();return new{needsSetup=await c.QuerySingleAsync<bool>("SELECT NOT EXISTS(SELECT 1 FROM staff)")};});
        app.MapGet("/api/auth/me",(ClaimsPrincipal u)=>new{name=u.FindFirstValue("name"),role=u.FindFirstValue(ClaimTypes.Role),id=u.FindFirstValue("sub")}).RequireAuthorization();
        app.MapPost("/api/auth/setup",async(AccountInput input,NpgsqlDataSource source,HttpContext context)=>{
            if(!app.Environment.IsDevelopment() && app.Configuration["Bootstrap:Enabled"]!="true") return Results.Json(new{error="Owner setup is restricted to the private deployment setup process."},statusCode:403);
            Validate(input);await using var c=await source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
            await c.ExecuteAsync("SELECT pg_advisory_xact_lock(994621)",transaction:tx);
            if(await c.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM staff)",transaction:tx))return Results.Conflict(new{error="Owner account already exists. Sign in instead."});
            var venue=Guid.NewGuid();var user=new Staff{Id=Guid.NewGuid(),VenueId=venue,Email=input.Email.Trim().ToLowerInvariant(),Name=input.Name.Trim(),Role="Owner"};user.PasswordHash=new PasswordHasher<Staff>().HashPassword(user,input.Password);
            await c.ExecuteAsync("INSERT INTO venues(id,name) VALUES(@venue,'Dragon Lord');INSERT INTO staff VALUES(@Id,@VenueId,@Email,@Name,@PasswordHash,@Role)",new{venue,user.Id,user.VenueId,user.Email,user.Name,user.PasswordHash,user.Role},tx);
            for(var i=1;i<=14;i++)await c.ExecuteAsync("INSERT INTO stations(id,venue_id,zone,hourly_minor,games) VALUES(@id,@venue,@zone,@rate,@games)",new{id=i<=12?$"PC-{i:00}":$"PS5-{i-12}",venue,zone=i<=8?"Standard":i<=12?"VIP":"Console",rate=i<=8?10000:i<=12?15000:20000,games=i<=12?"Valorant, Counter-Strike 2, Fortnite, Dota 2":"EA Sports FC, Gran Turismo 7"},tx);
            await tx.CommitAsync();await SignIn(context,user);return Results.Ok(new{user.Name,user.Role});
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/login",async(LoginInput input,NpgsqlDataSource source,HttpContext context)=>{
            await using var c=await source.OpenConnectionAsync();var user=await c.QuerySingleOrDefaultAsync<Staff>("SELECT * FROM staff WHERE email=@email",new{email=input.Email.Trim().ToLowerInvariant()});
            if(user is null||new PasswordHasher<Staff>().VerifyHashedPassword(user,user.PasswordHash,input.Password)==PasswordVerificationResult.Failed)return Results.Json(new{error="Email or password is incorrect."},statusCode:401);
            await SignIn(context,user);return Results.Ok(new{user.Name,user.Role});
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout",async(HttpContext context)=>{await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);return Results.Ok();});
        app.MapPost("/api/staff",async(AccountInput input,ClaimsPrincipal principal,NpgsqlDataSource source)=>{
            Validate(input);if(input.Role is not ("Manager" or "Cashier"))throw new ArgumentException("Choose Manager or Cashier.");
            var user=new Staff{Id=Guid.NewGuid(),VenueId=Guid.Parse(principal.FindFirstValue("venue_id")!),Email=input.Email.Trim().ToLowerInvariant(),Name=input.Name.Trim(),Role=input.Role};user.PasswordHash=new PasswordHasher<Staff>().HashPassword(user,input.Password);
            await using var c=await source.OpenConnectionAsync();await c.ExecuteAsync("INSERT INTO staff VALUES(@Id,@VenueId,@Email,@Name,@PasswordHash,@Role)",user);return Results.Ok(new{user.Id,user.Name,user.Role});
        }).RequireAuthorization("Owner");
    }
    private static void Validate(AccountInput i){if(string.IsNullOrWhiteSpace(i.Name)||i.Name.Length>120||!i.Email.Contains('@')||i.Email.Length>200||i.Password.Length<12||i.Password.Length>200)throw new ArgumentException("Provide a name, email, and a password of 12–200 characters.");}
    private static Task SignIn(HttpContext context,Staff u)=>context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim("sub",u.Id.ToString()),new Claim("venue_id",u.VenueId.ToString()),new Claim("name",u.Name),new Claim(ClaimTypes.Role,u.Role)},CookieAuthenticationDefaults.AuthenticationScheme)),new AuthenticationProperties{IsPersistent=false,ExpiresUtc=DateTimeOffset.UtcNow.AddHours(8)});
    private sealed class Staff{public Guid Id{get;set;}public Guid VenueId{get;set;}public string Email{get;set;}="";public string Name{get;set;}="";public string PasswordHash{get;set;}="";public string Role{get;set;}="";}
}
public record AccountInput(string Name,string Email,string Password,string Role="Owner");
public record LoginInput(string Email,string Password);
