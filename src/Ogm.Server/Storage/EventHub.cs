using System.Collections.Concurrent;
using System.Threading.Channels;
using Ogm.Shared;

namespace Ogm.Server.Storage;

/// <summary>
/// Panele gercek zamanli veri itmek icin basit bir yayin/abone merkezi.
/// Her SSE baglantisi bir kanal (channel) alir. Kanal dolarsa en eski olay
/// dusurulur; boylece yavas bir istemci sunucuyu yavaslatmaz.
/// </summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<ServerEvent>> _subscribers = new();

    /// <summary>Yeni bir abone olusturur ve kanal okuyucusunu dondurur.</summary>
    public (Guid Id, ChannelReader<ServerEvent> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(capacity: 32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    /// <summary>Aboneligi sonlandirir.</summary>
    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    /// <summary>Olayi tum abonelere yayinlar.</summary>
    public void Publish(ServerEvent serverEvent)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(serverEvent);
    }
}
