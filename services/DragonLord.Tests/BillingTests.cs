using DragonLord.Domain;
using Xunit;
public class BillingTests
{
    [Fact] public void RoundsUpOneStartedMinute(){var t=DateTimeOffset.UtcNow;Assert.Equal(167L,Billing.Charge(10000,10000,t,t.AddSeconds(1)));}
    [Fact] public void StopsAtPrepaidLimit(){var t=DateTimeOffset.UtcNow;Assert.Equal(10000L,Billing.Charge(10000,10000,t,t.AddDays(1)));}
    [Fact] public void ExcludesInterruption(){var t=DateTimeOffset.UtcNow;Assert.Equal(5000L,Billing.Charge(10000,10000,t,t.AddMinutes(40),600));}
    [Fact] public void AdjacentBookingsDoNotOverlap(){var t=DateTimeOffset.UtcNow;Assert.False(Billing.Overlaps(t,t.AddHours(1),t.AddHours(1),t.AddHours(2)));Assert.True(Billing.Overlaps(t,t.AddHours(1),t.AddMinutes(59),t.AddHours(2)));}
    [Fact] public void ReserveRejectsNegative(){Assert.Throws<ArgumentException>(()=>Billing.Reserve(10000,-1));}
}
