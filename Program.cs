namespace DnsUpdater;

internal sealed class Program
{
    private static async Task Main()
    {
        IIpAddressDiscoveryService ipAddressDiscoveryService = new NoipIpAddressDiscoveryService();

        IUpdater[] updaters = new IUpdater[]
        {
            new NoipDnsUpdater(ipAddressDiscoveryService),
        };

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6.0));

        do
        {
            foreach (IUpdater updater in updaters)
            {
                Console.WriteLine($"Updating {updater.Name}...");
                await updater.Update();
            }
        } while (await timer.WaitForNextTickAsync());
    }
}
