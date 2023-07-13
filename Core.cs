namespace DnsUpdater;

public record struct IpAddressAlias(string Value)
{
    public static implicit operator string(IpAddressAlias alias) => alias.Value;
    public static implicit operator IpAddressAlias(string alias) => new IpAddressAlias(alias);
}

public record struct HostnameAlias(string Value)
{
    public static implicit operator string(HostnameAlias alias) => alias.Value;
    public static implicit operator HostnameAlias(string alias) => new HostnameAlias(alias);
}
