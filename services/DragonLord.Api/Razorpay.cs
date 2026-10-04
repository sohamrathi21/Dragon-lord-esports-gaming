using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace DragonLord.Api;

public sealed class Razorpay(HttpClient http,IConfiguration config,NpgsqlDataSource source)
{
    public bool Configured => !string.IsNullOrEmpty(config["Razorpay:KeyId"])&&!string.IsNullOrEmpty(config["Razorpay:KeySecret"])&&!string.IsNullOrEmpty(config["Razorpay:WebhookSecret"]);
    public string KeyId => config["Razorpay:KeyId"]??"";
    public static bool ValidSignature(byte[] payload,string signature,string secret)
    {
        if(signature.Length!=64)return false;
        try{return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),payload),Convert.FromHexString(signature));}catch(FormatException){return false;}
    }
    private void Guard()
    {
        if(!Configured)throw new ArgumentException("Razorpay is not configured. Set server-side test keys and webhook secret.");
        if(KeyId.StartsWith("rzp_live_",StringComparison.Ordinal)&&config["Razorpay:AllowLive"]!="true")throw new ArgumentException("Live payments are disabled.");
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(KeyId+":"+config["Razorpay:KeySecret"])));
    }
    public async Task<object> CreateOrder(Guid venue,Guid customer,long amount,Guid intent,string actor)
    {
        Guard();if(amount is <100 or >10000000)throw new ArgumentException("Top-up must be ₹1–₹100,000.");
        await using var db=await source.OpenConnectionAsync();
        // Persist intent before contacting Razorpay. An ambiguous timeout stays Creating for reconciliation;
        // never create a second provider order for the same intent automatically.
        await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT id FROM venues WHERE id=@venue FOR UPDATE",new{venue},tx);
        var prior=await db.QuerySingleOrDefaultAsync<PaymentOrder>("SELECT * FROM payment_orders WHERE id=@intent",new{intent},tx);
        if(prior is not null){if(prior.VenueId!=venue||prior.CustomerId!=customer||prior.AmountMinor!=amount)throw new ArgumentException("Payment intent was reused with different details.");if(prior.ProviderOrderId is null)throw new ArgumentException("Order creation is pending reconciliation. Do not pay again.");return new{keyId=KeyId,orderId=prior.ProviderOrderId,amount=prior.AmountMinor,currency="INR",intentId=intent};}
        await db.ExecuteAsync("INSERT INTO payment_orders(id,venue_id,customer_id,amount_minor) VALUES(@intent,@venue,@customer,@amount)",new{intent,venue,customer,amount},tx);await tx.CommitAsync();
        var response=await http.PostAsJsonAsync("https://api.razorpay.com/v1/orders",new{amount,currency="INR",receipt=intent.ToString("N"),notes=new{intentId=intent.ToString()}});
        response.EnsureSuccessStatusCode();using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var orderId=document.RootElement.GetProperty("id").GetString()!;
        await db.ExecuteAsync("UPDATE payment_orders SET provider_order_id=@orderId,status='Created' WHERE id=@intent",new{orderId,intent});
        return new{keyId=KeyId,orderId,amount,currency="INR",intentId=intent};
    }
    public async Task<object> VerifyCheckout(Guid venue,Guid intent,string paymentId,string signature)
    {
        Guard();await using var db=await source.OpenConnectionAsync();
        var order=await db.QuerySingleAsync<PaymentOrder>("SELECT * FROM payment_orders WHERE id=@intent AND venue_id=@venue",new{intent,venue});
        // Order ID is taken from our database, never trusted from the checkout callback.
        if(order.ProviderOrderId is null||!ValidSignature(Encoding.UTF8.GetBytes(order.ProviderOrderId+"|"+paymentId),signature,config["Razorpay:KeySecret"]!))throw new ArgumentException("Invalid Razorpay checkout signature.");
        return await Capture("checkout:"+paymentId,paymentId);
    }
    public async Task<object> Webhook(byte[] original,string signature,string eventId)
    {
        Guard();if(!ValidSignature(original,signature,config["Razorpay:WebhookSecret"]!))throw new ArgumentException("Invalid Razorpay webhook signature.");
        if(string.IsNullOrWhiteSpace(eventId)||eventId.Length>200)throw new ArgumentException("Missing webhook event ID.");
        using var doc=JsonDocument.Parse(original);var root=doc.RootElement;
        if(root.GetProperty("event").GetString()!="payment.captured")return new{ignored=true};
        var paymentId=root.GetProperty("payload").GetProperty("payment").GetProperty("entity").GetProperty("id").GetString()!;
        return await Capture(eventId,paymentId);
    }
    private async Task<object> Capture(string eventId,string paymentId)
    {
        if(!paymentId.StartsWith("pay_",StringComparison.Ordinal)||paymentId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))throw new ArgumentException("Invalid payment reference.");
        // Fetch using our merchant credentials; enforce captured status, order, amount and currency.
        var response=await http.GetAsync("https://api.razorpay.com/v1/payments/"+paymentId);response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var p=doc.RootElement;
        if(p.GetProperty("status").GetString()!="captured")return new{status="PendingCapture"};
        var orderId=p.GetProperty("order_id").GetString();await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
        var order=await db.QuerySingleOrDefaultAsync<PaymentOrder>("SELECT * FROM payment_orders WHERE provider_order_id=@orderId FOR UPDATE",new{orderId},tx)??throw new ArgumentException("Unknown merchant order.");
        if(p.GetProperty("amount").GetInt64()!=order.AmountMinor||p.GetProperty("currency").GetString()!=order.Currency)throw new ArgumentException("Payment amount or currency mismatch.");
        await db.ExecuteAsync("SELECT id FROM venues WHERE id=@venue FOR UPDATE",new{venue=order.VenueId},tx);
        if(order.Status=="Captured"){await tx.CommitAsync();return new{status="Captured",duplicate=true};}
        await Store.Post(db,tx,order.VenueId,order.CustomerId,order.AmountMinor,"Payment","razorpay:"+paymentId,"Razorpay verified");
        await db.ExecuteAsync("UPDATE payment_orders SET status='Captured',provider_payment_id=@paymentId WHERE id=@id;INSERT INTO provider_events VALUES(@eventId,@paymentId,now()) ON CONFLICT DO NOTHING",new{id=order.Id,eventId,paymentId},tx);
        var payload=JsonSerializer.Serialize(new{orderId=order.Id,customerId=order.CustomerId,amountMinor=order.AmountMinor,paymentId});
        await db.ExecuteAsync("INSERT INTO audit(venue_id,actor,action,detail) VALUES(@venue,'Razorpay','PaymentCaptured',CAST(@payload AS jsonb));INSERT INTO outbox(id,venue_id,kind,payload) VALUES(@id,@venue,'payment.captured',CAST(@payload AS jsonb))",new{venue=order.VenueId,id=Guid.NewGuid(),payload},tx);
        await tx.CommitAsync();return new{status="Captured",amountMinor=order.AmountMinor};
    }
    private sealed class PaymentOrder
    {
        public Guid Id{get;set;}public Guid VenueId{get;set;}public Guid CustomerId{get;set;}public long AmountMinor{get;set;}public string Currency{get;set;}="INR";public string? ProviderOrderId{get;set;}public string Status{get;set;}="";
    }
}
public record RazorpayOrderInput(Guid CustomerId,long AmountMinor,Guid IntentId);
public record RazorpayVerifyInput(Guid IntentId,string PaymentId,string Signature);
