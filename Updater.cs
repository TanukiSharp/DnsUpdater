namespace DnsUpdater;

public interface IUpdater
{
    string Name { get; }
    ValueTask Update();
}
