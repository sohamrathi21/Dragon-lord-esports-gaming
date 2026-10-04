namespace DragonLord.Domain;

public static class Billing
{
    public static long Reserve(long hourlyMinor, int minutes)
    {
        if (hourlyMinor <= 0 || minutes is < 1 or > 600) throw new ArgumentException("Invalid rate or duration.");
        return checked((hourlyMinor * minutes + 59) / 60);
    }
    public static long Charge(long hourlyMinor, long reservedMinor, DateTimeOffset start, DateTimeOffset now, long pausedSeconds = 0)
    {
        if (hourlyMinor <= 0 || reservedMinor < 0 || pausedSeconds < 0) throw new ArgumentException("Invalid charge inputs.");
        var elapsed = Math.Max(0, (now - start).TotalSeconds - pausedSeconds);
        var minutes = (long)Math.Ceiling(elapsed / 60);
        return Math.Min(reservedMinor, checked((hourlyMinor * minutes + 59) / 60));
    }
    public static bool Overlaps(DateTimeOffset a, DateTimeOffset b, DateTimeOffset c, DateTimeOffset d) => a < d && b > c;
}
public record CustomerInput(string Name, string? Email, bool Member);
public record CashPayment(Guid CustomerId, long AmountMinor, string IdempotencyKey);
public record StartSession(Guid CustomerId, string StationId, int Minutes, string IdempotencyKey);
public record EndSession(string Reason);
public record ReservationInput(Guid CustomerId, string[] StationIds, DateTimeOffset Start, DateTimeOffset End, string IdempotencyKey);
public record CreditInput(Guid CustomerId, long AmountMinor, string Reason, string IdempotencyKey);
public record OrderInput(Guid CustomerId, string StationId, Guid ProductId, int Quantity, string IdempotencyKey);
public record VerifiedPayment(string Provider, string EventId, string PaymentId, Guid CustomerId, long AmountMinor, string Currency);
public interface IPaymentProvider
{
    // Verify signature over the original bytes; validate merchant, captured status, order, amount and currency.
    Task<VerifiedPayment> VerifyAsync(ReadOnlyMemory<byte> body, string signature, CancellationToken ct);
}
public interface IVenueAcknowledgement
{
    Task<bool> ConfirmReservationAsync(Guid venueId, Guid reservationId, CancellationToken ct);
}
