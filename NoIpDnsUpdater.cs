using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace DnsUpdater;

public record struct DnsUpdateConfigurationElement(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("hostnames")] string[] Hostnames,
    [property: JsonPropertyName("ipAddress")] string? IpAddress
)
{
    public override string ToString()
    {
        return $"username: '{Username}' password: *{Password.Length}* hostnames: '{string.Join(',', Hostnames)}' ipAddress: {(IpAddress != null ? $"'{IpAddress}'" : "null")}";
    }
}

public sealed class NoipDnsUpdater : IUpdater
{
    private static readonly HttpClient httpClient;
    private static readonly DnsUpdateConfigurationElement[] dnsUpdateConfiguration;

    private readonly ILogger logger;
    private readonly IIpAddressDiscoveryService ipAddressDiscoveryService;
    private readonly StorageContainer<Dictionary<HostnameAlias, IpAddressAlias>> databaseContainer;

    private static readonly Dictionary<string, string> UserErrors = new()
    {
        ["nohost"] = "Hostname supplied does not exist under specified account, client exit and require user to enter new login credentials before performing an additional request.",
        ["badauth"] = "Invalid username password combination.",
        ["badagent"] = """
            Client disabled. Client should exit and not perform any more updates without user intervention.
            Note: You must use the recommended User - Agent format specified when Submitting an Update,
            failure to follow the format guidelines may result in your client being blocked.
        """,
        ["!donator"] = "An update request was sent, including a feature that is not available to that particular user such as offline options.",
        ["abuse"] = "Username is blocked due to abuse. Either for not following our update specifications or disabled due to violation of the No-IP terms of service. Our terms of service can be viewed here. Client should stop sending updates.",
    };

    private static readonly Dictionary<string, string> ServerErrors = new()
    {
        ["911"] = """
            A fatal error on our side such as a database outage. Retry the update no sooner than 30 minutes.
            A 500 HTTP error may also be returned in case of a fatal error on our system at which point you should also retry no sooner than 30 minutes.
        """,
    };

    static NoipDnsUpdater()
    {
        httpClient = SetupHttpClient();
        dnsUpdateConfiguration = SetupDnsUpdateConfiguration();
    }

    public NoipDnsUpdater(IIpAddressDiscoveryService ipAddressDiscoveryService)
    {
        logger = new LoggerFactory().CreateLogger<NoipDnsUpdater>();

        this.ipAddressDiscoveryService = ipAddressDiscoveryService;
        databaseContainer = new StorageContainer<Dictionary<HostnameAlias, IpAddressAlias>>("noip", "db.json");
    }

    private static HttpClient SetupHttpClient()
    {
        var httpClient = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(15.0),
        };

        Version? appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        string version = appVersion == null ? "v?" : $"v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}";
        string userAgent = $"TanukiSharp DnsUpdater/{RuntimeInformation.RuntimeIdentifier}-{version} sebastien.saigo@gmail.com";

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

        return httpClient;
    }

    private static DnsUpdateConfigurationElement[] SetupDnsUpdateConfiguration()
    {
        string filename = Path.Join(AppContext.BaseDirectory, "configs", "noip.com", "config.json");

        if (File.Exists(filename) == false)
            throw new FileNotFoundException($"Could not find file '{filename}'.", filename);

        using Stream fs = File.OpenRead(filename);

        DnsUpdateConfigurationElement[]? result = JsonSerializer.Deserialize<DnsUpdateConfigurationElement[]>(fs);

        if (result == null)
            throw new InvalidDataException($"Could not load JSON data from file '{filename}'.");

        if (result.Length == 0)
            throw new InvalidDataException($"List of DNS update info in file '{filename}' is empty.");

        for (int i = 0; i < result.Length; i++)
        {
            DnsUpdateConfigurationElement dnsUpdateConfigurationElement = result[i];
            CheckDnsUpdateInfo(i, filename, dnsUpdateConfigurationElement);
        }

        return result;
    }

    private static void CheckDnsUpdateInfo(int index, string filename, DnsUpdateConfigurationElement dnsUpdateConfigurationElement)
    {
        if (string.IsNullOrEmpty(dnsUpdateConfigurationElement.Username))
            throw new InvalidDataException($"Element {index} in file '{filename}' has no or invalid 'username' field.");

        if (string.IsNullOrEmpty(dnsUpdateConfigurationElement.Password))
            throw new InvalidDataException($"Element {index} in file '{filename}' has no or invalid 'password' field.");

        if (dnsUpdateConfigurationElement.Hostnames == null)
            throw new InvalidDataException($"Element {index} in file '{filename}' has no or invalid 'hostnames' field.");

        if (dnsUpdateConfigurationElement.Hostnames.Length == 0)
            throw new InvalidDataException($"Element {index} in file '{filename}' has empty 'hostnames' field.");

        for (int j = 0; j < dnsUpdateConfigurationElement.Hostnames.Length; j++)
        {
            string hostname = dnsUpdateConfigurationElement.Hostnames[j];

            if (string.IsNullOrWhiteSpace(hostname))
                throw new InvalidDataException($"Element {index} in file '{filename}' has invalid 'hostnames[{j}]' value.");
        }

        if (dnsUpdateConfigurationElement.IpAddress != null) {
            if (string.IsNullOrWhiteSpace(dnsUpdateConfigurationElement.IpAddress))
                throw new InvalidDataException($"Element {index} in file '{filename}' has empty 'ipAddress' field. Can be omitted or be null but not empty or contain only white spaces.");
        }
    }

    public string Name { get; } = "noip.com DNS service";

    private static bool ShouldUpdate(string? detectedIpAddress, string? ipInDatabase, string? desiredIpAddress)
    {
        if (detectedIpAddress == null || ipInDatabase == null)
            return true;

        if (desiredIpAddress == null)
            return detectedIpAddress != ipInDatabase;

        if (desiredIpAddress != detectedIpAddress)
            return true;

        return false;
    }

    public async ValueTask Update()
    {
        IpAddressInfo detectedIpAddressInfo = await ipAddressDiscoveryService.GetIpAddress();

        Dictionary<HostnameAlias, IpAddressAlias> db = (await databaseContainer.Get()) ?? new();

        foreach (DnsUpdateConfigurationElement dnsUpdateConfigurationElement in dnsUpdateConfiguration)
        {
            await UpdateElement(dnsUpdateConfigurationElement, detectedIpAddressInfo, db);
        }
    }

    private static bool ShouldUpdateHostname(
        string hostname,
        DnsUpdateConfigurationElement dnsUpdateConfigurationElement,
        IpAddressInfo detectedIpAddressInfo,
        Dictionary<HostnameAlias, IpAddressAlias> db
    )
    {
        db.TryGetValue(hostname, out IpAddressAlias ipAddressInDatabase);

        return ShouldUpdate(
            detectedIpAddressInfo.IpAddress,
            ipAddressInDatabase,
            dnsUpdateConfigurationElement.IpAddress
        );
    }

    private async ValueTask UpdateElement(
        DnsUpdateConfigurationElement dnsUpdateConfigurationElement,
        IpAddressInfo detectedIpAddressInfo,
        Dictionary<HostnameAlias, IpAddressAlias> db
    )
    {
        var hostnamesToUpdate = new List<string>();

        foreach (string hostname in dnsUpdateConfigurationElement.Hostnames)
        {
            db.TryGetValue(hostname, out IpAddressAlias ipAddressInDatabase);

            bool shouldUpdate = ShouldUpdate(
                detectedIpAddressInfo.IpAddress,
                ipAddressInDatabase,
                dnsUpdateConfigurationElement.IpAddress
            );

            if (shouldUpdate)
                hostnamesToUpdate.Add(hostname);
        }

        HttpRequestMessage requestMessage = CreateRequestMessage(hostnamesToUpdate, dnsUpdateConfigurationElement);
        HttpResponseMessage responseMessage = await httpClient.SendAsync(requestMessage);
        string? responseContent = await ReadResponseMessage(responseMessage);

        if (responseContent == null)
            return;

        ServerResponseType[] parsedResponseContent = ParseResponseContent(responseContent);

        ProcessResponseContent(hostnamesToUpdate, parsedResponseContent);
    }

    private static HttpRequestMessage CreateRequestMessage(
        List<string> hostnamesToUpdate,
        DnsUpdateConfigurationElement dnsUpdateInfo
    )
    {
        string hostnames = string.Join(',', hostnamesToUpdate);

        string url = $"https://dynupdate.no-ip.com/nic/update?hostname={HttpUtility.UrlEncode(hostnames)}";

        if (dnsUpdateInfo.IpAddress != null)
            url += $"&myip={HttpUtility.UrlEncode(dnsUpdateInfo.IpAddress)}";

        var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{dnsUpdateInfo.Username}:{dnsUpdateInfo.Password}")));

        return requestMessage;
    }

    private async ValueTask<string?> ReadResponseMessage(HttpResponseMessage responseMessage)
    {
        string responseContent = await responseMessage.Content.ReadAsStringAsync();

        if (responseContent == null)
        {
            logger.LogError("response: status={StatusCode}, content=null", responseMessage.StatusCode);
            return null;
        }

        logger.LogInformation("response: status={StatusCode}, content={responseContent}", responseMessage.StatusCode, responseContent);

        return responseContent;
    }

    private bool ProcessResponseContent(
        string ipAddress,
        List<string> hostnamesToUpdate,
        ServerResponseType[] responseContent,
        Dictionary<HostnameAlias, IpAddressAlias> db
    )
    {
        if (hostnamesToUpdate.Count != responseContent.Length)
        {
            logger.LogError("Provided {hostnameCount} to server and server responded with {resultCount} results.", hostnamesToUpdate.Count, responseContent.Length);
            return true;
        }

        for (int i = 0; i < hostnamesToUpdate.Count; i++)
        {
            if (responseContent[i] == ServerResponseType.NoChange || responseContent[i] == ServerResponseType.Update)
                db[hostnamesToUpdate[i]] = ipAddress;
        }

        for (int i = 0; i < hostnamesToUpdate.Count; i++)
        {
            if (responseContent[i] == ServerResponseType.UserError)
                return false;
        }

        return true;
    }

    private enum ServerResponseType
    {
        Update,
        NoChange,
        ServerError,
        UserError,
        Unsupported,
    }

    private ServerResponseType ParseResponseContentSingleLine(string responseContentLine)
    {
        if (responseContentLine.StartsWith("good "))
            return ServerResponseType.Update;

        if (responseContentLine.StartsWith("nochg "))
            return ServerResponseType.NoChange;

        if (ServerErrors.TryGetValue(responseContentLine, out string? message))
        {
            logger.LogWarning("Server responded with server error: '{message}'.", message);
            return ServerResponseType.ServerError;
        }

        if (UserErrors.TryGetValue(responseContentLine, out message))
        {
            logger.LogError("Server responded with user error: '{message}'.", message);
            return ServerResponseType.UserError;
        }

        logger.LogWarning("Unsupported response '{response}'.", responseContentLine);

        return ServerResponseType.Unsupported;
    }

    private static readonly char[] NewLineSeparators = new char[] { '\r', '\n' };

    private ServerResponseType[] ParseResponseContent(string responseContent)
    {
        string[] lines = responseContent.Split(NewLineSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var response = new ServerResponseType[lines.Length];

        for (int i = 0; i < lines.Length; i++)
            response[i] = ParseResponseContentSingleLine(lines[i]);

        return response;
    }
}
