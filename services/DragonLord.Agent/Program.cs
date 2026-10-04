using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
var builder=Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o=>o.ServiceName="Dragon Lord Agent Simulator");
builder.Services.AddHostedService<AgentSimulator>();
await builder.Build().RunAsync();
// Deliberately no privileged control commands. Provision mTLS device identity before replacing this adapter.
sealed class AgentSimulator(ILogger<AgentSimulator> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
        log.LogWarning("SIMULATOR: no device lock, launch, credential collection, or update execution is enabled.");
        while(await timer.WaitForNextTickAsync(stop))log.LogInformation("Simulated heartbeat {Timestamp}; station {Station}",DateTimeOffset.UtcNow,Environment.GetEnvironmentVariable("DRAGON_STATION_ID")??"PC-01");
    }
}
