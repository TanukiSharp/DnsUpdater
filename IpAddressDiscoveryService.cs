using System.Text.Json.Serialization;

namespace DnsUpdater;

public enum DataSource
{
    None,
    Storage,
    Service,
}

public readonly record struct IpAddressInfo(string? IpAddress, DateTimeOffset LastTimeChecked, DataSource Source)
{
    public bool IsValid
    {
        get
        {
            return IpAddress != null && Source != DataSource.None;
        }
    }
}

public interface IIpAddressDiscoveryService
{
    ValueTask<IpAddressInfo> GetIpAddress();
}

internal record NoipSettings(
    [property: JsonPropertyName("ipAddress")] string IpAddress,
    [property: JsonPropertyName("lastTimeChecked")] DateTimeOffset LastTimeChecked
);

public sealed class NoipIpAddressDiscoveryService : IIpAddressDiscoveryService
{
    private static readonly HttpClient httpClient;
    private static readonly StorageContainer<NoipSettings> storageContainer;

    static NoipIpAddressDiscoveryService()
    {
        httpClient = SetupHttpClient();

        storageContainer = new StorageContainer<NoipSettings>("noip", "settings.json");
    }

    private static HttpClient SetupHttpClient()
    {
        var httpClient = new HttpClient()
        {
            BaseAddress = new Uri("http://ip1.dynupdate.no-ip.com"),
            Timeout = TimeSpan.FromSeconds(15.0),
        };

        return httpClient;
    }

    private static async ValueTask<IpAddressInfo> GetIpAddressFromStorage()
    {
        NoipSettings? settings = await storageContainer.Get();

        if (settings == null)
            return new IpAddressInfo(default, default, DataSource.None);

        return new IpAddressInfo(settings.IpAddress, settings.LastTimeChecked, DataSource.Storage);
    }

    private static async ValueTask<IpAddressInfo> GetIpAddressFromService()
    {
        HttpResponseMessage responseMessage = await httpClient.GetAsync("");
        string responseContent = await responseMessage.Content.ReadAsStringAsync();

        if (responseMessage.IsSuccessStatusCode == false)
        {
            Console.WriteLine($"[WARNING] Failed to fetch IP address, response status {responseMessage.StatusCode}, response content: {responseContent}");
            return new IpAddressInfo(default, default, DataSource.None);
        }

        return new IpAddressInfo(responseContent, DateTimeOffset.Now, DataSource.Service);
    }

    public async ValueTask<IpAddressInfo> GetIpAddress()
    {
        IpAddressInfo ipAddressInfo = await GetIpAddressFromStorage();

        var oneHourAgo = DateTimeOffset.Now.Add(TimeSpan.FromHours(-1.0));

        if (ipAddressInfo.Source == DataSource.None || ipAddressInfo.LastTimeChecked <= oneHourAgo)
        {
            ipAddressInfo = await GetIpAddressFromService();

            if (ipAddressInfo.IsValid)
                await storageContainer.Set(new NoipSettings(ipAddressInfo.IpAddress!, ipAddressInfo.LastTimeChecked));
        }

        return ipAddressInfo;
    }
}
