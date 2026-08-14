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
        // Id/Name/Version come from the csproj (PluginId, PluginDisplayName,
        // Version) via the SDK-generated PluginBuildInfo, always in sync with
        // the generated manifest.json
        Id = PluginBuildInfo.Id,
        Name = PluginBuildInfo.Name,
        Version = PluginBuildInfo.Version,
        Description = "Drives Shelly Gen1 color devices (RGBW2 and similar) from DMX data via MQTT.",
    };

    public Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        // One profile, one personality per protocol mode — lets the fixture
        // editor patch a Shelly with the right channel layout and prefill
        // from an existing mapping
        this.registrations.Add(host.Outputs.RegisterFixtureProfile(new PluginFixtureProfileDescriptor
        {
            Code = "SHELLY_COLOR",
            Name = "Gen1 Color (RGBW2)",
            Manufacturer = "Shelly",
            Personalities =
            [
                new PluginFixturePersonality
                {
                    Name = "RGB",
                    Channels = [PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue],
                },
                new PluginFixturePersonality
                {
                    Name = "RGBW",
                    Channels = [PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue, PluginFixtureFunction.White],
                },
                new PluginFixturePersonality
                {
                    Name = "RGBW+intensity",
                    Channels = [PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue, PluginFixtureFunction.White, PluginFixtureFunction.Intensity],
                },
            ],
        }));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor("SHELLYGEN1COLORRGB", "Shelly Gen1 Color RGB", "RGB"),
            new ShellyOutputProtocol(host, ShellyChannelMode.Rgb)));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor("SHELLYGEN1COLORRGBW", "Shelly Gen1 Color RGBW", "RGBW"),
            new ShellyOutputProtocol(host, ShellyChannelMode.Rgbw)));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor("SHELLYGEN1COLORRGBWI", "Shelly Gen1 Color RGBW+intensity", "RGBW+intensity"),
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

    private static OutputProtocolDescriptor Descriptor(string id, string displayName, string personality)
    {
        return new OutputProtocolDescriptor
        {
            Id = id,
            DisplayName = displayName,
            PortType = "SHELLY",
            PortTypeDisplayName = "Shelly",
            // Shelly Gen1 devices choke above ~10 commands/s
            MaxUpdatesPerSecond = 10,
            SupportsDestinationDiscovery = true,
            SuggestedProfileCode = "SHELLY_COLOR",
            SuggestedPersonality = personality,
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

    private static readonly HttpClient statusClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<IReadOnlyList<PluginOutputDestinationOption>?> GetDestinationOptionsAsync(bool refresh, CancellationToken cancellationToken)
    {
        // Gen1 devices announce over mDNS with the device id as the instance
        // name — the same string the MQTT topic uses
        var services = refresh
            ? await host.Mdns.RefreshServicesAsync("_http._tcp", cancellationToken)
            : await host.Mdns.GetServicesAsync("_http._tcp", cancellationToken);

        var shellies = services
            .Where(x => x.InstanceName.StartsWith("shelly", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.InstanceName)
            .ToList();

        // A discovered device only responds once its MQTT client points at
        // the broker this Core uses, so surface the device's actual MQTT
        // state right in the pick list (Gen1 exposes it over plain HTTP)
        var options = await Task.WhenAll(shellies.Select(async service =>
        {
            var details = new List<string>();

            if (!string.IsNullOrEmpty(service.Address))
            {
                details.Add(service.Address);

                string? mqttStatus = await ProbeMqttStatus(service.Address, cancellationToken);
                if (mqttStatus != null)
                    details.Add(mqttStatus);
            }

            string label = details.Count > 0
                ? $"{service.InstanceName} ({string.Join(", ", details)})"
                : service.InstanceName;

            return new PluginOutputDestinationOption(service.InstanceName, label);
        }));

        return options;
    }

    private static async Task<string?> ProbeMqttStatus(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await statusClient.GetAsync($"http://{address}/settings", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            if (!json.RootElement.TryGetProperty("mqtt", out var mqtt))
                return null;

            if (!(mqtt.TryGetProperty("enable", out var enable) && enable.GetBoolean()))
                return "MQTT off";

            string? server = mqtt.TryGetProperty("server", out var serverProperty) ? serverProperty.GetString() : null;

            return string.IsNullOrEmpty(server) ? "MQTT on" : $"MQTT → {server}";
        }
        catch
        {
            // Unreachable, protected settings, unexpected payload: no annotation
            return null;
        }
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
