using System.Globalization;
using DMXCore.PluginSdk;

namespace DMXCore100.ShellyPlugin;

/// <summary>
/// Shelly Gen1 color output protocols over MQTT — the plugin edition of the
/// former built-in Shelly output support. Registers the same protocol ids the
/// core used to store, so pre-plugin output configurations keep working
/// unchanged.
/// </summary>
/// <remarks>
/// The device publishes to <c>shellies/{deviceId}/color/0/set</c> on the
/// broker configured on the Core; the mapping's destination address is the
/// Shelly device id (the topic segment, e.g. "shellyrgbw2-A4CF12F45478").
/// </remarks>
public class ShellyPlugin : IPlugin
{
    private readonly List<IDisposable> registrations = [];

    public PluginInfo Info { get; } = new()
    {
        Id = "shelly",
        Name = "Shelly",
        Version = "1.0.0",
        Description = "Drives Shelly Gen1 color devices (RGBW2 and similar) from DMX data via MQTT.",
    };

    public Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor("SHELLYGEN1COLORRGB", "Shelly Gen1 Color RGB"),
            new ShellyOutputProtocol(host, ShellyChannelMode.Rgb)));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor("SHELLYGEN1COLORRGBW", "Shelly Gen1 Color RGBW"),
            new ShellyOutputProtocol(host, ShellyChannelMode.Rgbw)));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor("SHELLYGEN1COLORRGBWI", "Shelly Gen1 Color RGBW+intensity"),
            new ShellyOutputProtocol(host, ShellyChannelMode.RgbwIntensity)));

        // The integration is only as connected as the broker
        this.registrations.Add(host.Mqtt.OnConnectionChanged((connected, ct) =>
        {
            host.SetConnectionState(connected, connected ? null : "MQTT broker not connected");

            return Task.CompletedTask;
        }));

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in this.registrations)
        {
            registration.Dispose();
        }

        this.registrations.Clear();

        return Task.CompletedTask;
    }

    private static OutputProtocolDescriptor Descriptor(string id, string displayName)
    {
        return new OutputProtocolDescriptor
        {
            Id = id,
            DisplayName = displayName,
            PortType = "SHELLY",
            PortTypeDisplayName = "Shelly",
            // Shelly Gen1 devices choke above ~10 commands/s
            MaxUpdatesPerSecond = 10,
        };
    }
}

internal enum ShellyChannelMode
{
    Rgb = 3,
    Rgbw = 4,
    RgbwIntensity = 5,
}

internal sealed class ShellyOutputProtocol(IPluginHost host, ShellyChannelMode mode) : IPluginOutputProtocol
{
    public int GetChannelCount(PluginOutputMappingConfig config)
    {
        return (int)mode;
    }

    public Task<IPluginOutputSession> OpenSessionAsync(PluginOutputMappingConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationAddress))
            throw new InvalidOperationException("Destination address (the Shelly device id) is required");

        return Task.FromResult<IPluginOutputSession>(new ShellySession(host, mode, config.DestinationAddress));
    }
}

internal sealed class ShellySession(IPluginHost host, ShellyChannelMode mode, string deviceId) : IPluginOutputSession
{
    private readonly string topic = $"shellies/{deviceId}/color/0/set";

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        if (!host.Mqtt.IsConnected)
            // Not an error: retried with the latest values once the broker is back
            return false;

        byte red = channelValues.Span[0];
        byte green = channelValues.Span[1];
        byte blue = channelValues.Span[2];
        byte white = mode >= ShellyChannelMode.Rgbw ? channelValues.Span[3] : (byte)0;
        string gain = mode == ShellyChannelMode.RgbwIntensity
            ? (channelValues.Span[4] * 100.0 / 255.0).ToString("F0", CultureInfo.InvariantCulture)
            : "100";

        string payload = $@"{{""mode"":""color"",""red"":{red},""green"":{green},""blue"":{blue},""white"":{white},""gain"":{gain},""effect"":0,""turn"":""on""}}";

        await host.Mqtt.PublishAsync(this.topic, payload, retain: false, cancellationToken: cancellationToken);

        return true;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
