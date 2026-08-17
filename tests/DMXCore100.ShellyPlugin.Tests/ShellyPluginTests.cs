using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using System.Text.Json;

namespace DMXCore100.ShellyPlugin.Tests;

/// <summary>
/// The Shelly plugin against the SDK's in-memory TestPluginHost: what it
/// registers (the protocol and profile ids that pre-plugin output
/// configurations depend on), the exact MQTT command it publishes for each
/// channel mode, how it behaves while the broker is down, and how it
/// discovers devices over mDNS.
/// </summary>
[TestClass]
public class ShellyPluginTests
{
    private const string DeviceId = "shellyrgbw2-A4CF12F45478";

    private readonly List<ShellyPlugin> plugins = [];

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (var plugin in this.plugins)
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }

        this.plugins.Clear();
    }

    private async Task<(ShellyPlugin Plugin, TestPluginHost Host)> CreateInitializedAsync()
    {
        var host = new TestPluginHost();
        var plugin = new ShellyPlugin();
        this.plugins.Add(plugin);

        await plugin.InitializeAsync(host, CancellationToken.None);

        return (plugin, host);
    }

    private static PluginOutputMappingConfig Mapping(string? destination = DeviceId)
    {
        return new PluginOutputMappingConfig
        {
            DestinationAddress = destination,
            ChannelOffset = 0,
            UniverseId = 1,
        };
    }

    private static JsonElement ParsePayload(string payload)
    {
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    [TestMethod]
    public void Info_ComesFromTheProjectFile()
    {
        var plugin = new ShellyPlugin();

        Assert.AreEqual("shelly", plugin.Info.Id);
        Assert.AreEqual("Shelly", plugin.Info.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(plugin.Info.Version));
    }

    [TestMethod]
    public async Task Initialize_RegistersTheLegacyProtocolIdsAndProfile()
    {
        var (_, host) = await CreateInitializedAsync();

        // These ids are what the core stored before Shelly became a plugin —
        // renaming any of them silently breaks existing output configs
        CollectionAssert.AreEquivalent(
            new[] { "SHELLYGEN1COLORRGB", "SHELLYGEN1COLORRGBW", "SHELLYGEN1COLORRGBWI" },
            host.OutputProtocols.Keys.ToArray());

        foreach (var (descriptor, protocol) in host.OutputProtocols.Values)
        {
            Assert.AreEqual("SHELLY", descriptor.PortType);
            Assert.AreEqual(10, descriptor.MaxUpdatesPerSecond, "Gen1 devices choke above ~10 commands/s");
            Assert.IsTrue(descriptor.SupportsDestinationDiscovery);
            Assert.AreEqual("SHELLY_COLOR", descriptor.SuggestedProfileCode);
            Assert.IsNotNull(protocol);
        }

        Assert.AreEqual(3, host.OutputProtocols["SHELLYGEN1COLORRGB"].Protocol.GetChannelCount(Mapping()));
        Assert.AreEqual(4, host.OutputProtocols["SHELLYGEN1COLORRGBW"].Protocol.GetChannelCount(Mapping()));
        Assert.AreEqual(5, host.OutputProtocols["SHELLYGEN1COLORRGBWI"].Protocol.GetChannelCount(Mapping()));

        Assert.IsTrue(host.FixtureProfiles.TryGetValue("SHELLY_COLOR", out var profile));
        CollectionAssert.AreEqual(new[] { "RGB", "RGBW", "RGBW+intensity" }, profile.Personalities.Select(x => x.Name).ToArray());
        Assert.AreEqual(3, profile.Personalities[0].Channels.Count);
        Assert.AreEqual(4, profile.Personalities[1].Channels.Count);
        Assert.AreEqual(5, profile.Personalities[2].Channels.Count);

        // Each protocol suggests the matching personality so the fixture
        // editor prefills the right channel layout
        Assert.AreEqual("RGB", host.OutputProtocols["SHELLYGEN1COLORRGB"].Descriptor.SuggestedPersonality);
        Assert.AreEqual("RGBW", host.OutputProtocols["SHELLYGEN1COLORRGBW"].Descriptor.SuggestedPersonality);
        Assert.AreEqual("RGBW+intensity", host.OutputProtocols["SHELLYGEN1COLORRGBWI"].Descriptor.SuggestedPersonality);
    }

    [TestMethod]
    public async Task Shutdown_UnregistersEverything()
    {
        var (plugin, host) = await CreateInitializedAsync();

        await plugin.ShutdownAsync(CancellationToken.None);

        Assert.AreEqual(0, host.OutputProtocols.Count);
        Assert.AreEqual(0, host.FixtureProfiles.Count);
    }

    [TestMethod]
    public async Task Send_Rgb_PublishesColorCommandToTheDeviceTopic()
    {
        var (_, host) = await CreateInitializedAsync();

        bool sent = await host.SimulateOutputDeliveryAsync("SHELLYGEN1COLORRGB", Mapping(), [255, 128, 0]);

        Assert.IsTrue(sent);
        Assert.AreEqual(1, host.PublishedMessages.Count);

        var (topic, payload, retain) = host.PublishedMessages[0];
        Assert.AreEqual($"shellies/{DeviceId}/color/0/set", topic);
        Assert.IsFalse(retain, "live color commands must never be retained");

        var json = ParsePayload(payload);
        Assert.AreEqual("color", json.GetProperty("mode").GetString());
        Assert.AreEqual(255, json.GetProperty("red").GetInt32());
        Assert.AreEqual(128, json.GetProperty("green").GetInt32());
        Assert.AreEqual(0, json.GetProperty("blue").GetInt32());
        Assert.AreEqual(0, json.GetProperty("white").GetInt32(), "RGB mode never drives the white channel");
        Assert.AreEqual(100, json.GetProperty("gain").GetInt32(), "RGB mode runs at full gain");
        Assert.AreEqual(0, json.GetProperty("effect").GetInt32());
        Assert.AreEqual("on", json.GetProperty("turn").GetString());
    }

    [TestMethod]
    public async Task Send_Rgbw_IncludesWhiteAtFullGain()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateOutputDeliveryAsync("SHELLYGEN1COLORRGBW", Mapping(), [10, 20, 30, 40]);

        var json = ParsePayload(host.PublishedMessages.Single().Payload);
        Assert.AreEqual(10, json.GetProperty("red").GetInt32());
        Assert.AreEqual(20, json.GetProperty("green").GetInt32());
        Assert.AreEqual(30, json.GetProperty("blue").GetInt32());
        Assert.AreEqual(40, json.GetProperty("white").GetInt32());
        Assert.AreEqual(100, json.GetProperty("gain").GetInt32());
    }

    [TestMethod]
    [DataRow(255, 100)]
    [DataRow(128, 50)]
    [DataRow(0, 0)]
    [DataRow(1, 0)]
    [DataRow(3, 1)]
    public async Task Send_RgbwIntensity_MapsTheFifthChannelToGainPercent(int intensity, int expectedGain)
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateOutputDeliveryAsync("SHELLYGEN1COLORRGBWI", Mapping(), [1, 2, 3, 4, (byte)intensity]);

        var json = ParsePayload(host.PublishedMessages.Single().Payload);
        Assert.AreEqual(4, json.GetProperty("white").GetInt32());
        Assert.AreEqual(expectedGain, json.GetProperty("gain").GetInt32());
    }

    [TestMethod]
    public async Task Send_WhileBrokerIsDown_ReturnsFalseWithoutPublishing()
    {
        var (_, host) = await CreateInitializedAsync();
        host.MqttConnected = false;

        bool sent = await host.SimulateOutputDeliveryAsync("SHELLYGEN1COLORRGB", Mapping(), [1, 2, 3]);

        // Not an error: the host retries with the latest values once the
        // broker is back
        Assert.IsFalse(sent);
        Assert.AreEqual(0, host.PublishedMessages.Count);
    }

    [TestMethod]
    public async Task OpenSession_WithoutDestination_Throws()
    {
        var (_, host) = await CreateInitializedAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            host.SimulateOutputDeliveryAsync("SHELLYGEN1COLORRGB", Mapping(destination: " "), [1, 2, 3]));
    }

    [TestMethod]
    public async Task ConnectionState_FollowsTheBroker()
    {
        var (_, host) = await CreateInitializedAsync();

        // The SDK contract delivers the current state on subscription
        Assert.AreEqual(true, host.ConnectionState);

        await host.SimulateMqttConnectionChangedAsync(false);
        Assert.AreEqual(false, host.ConnectionState);
        Assert.AreEqual("MQTT broker not connected", host.ConnectionDetail);

        await host.SimulateMqttConnectionChangedAsync(true);
        Assert.AreEqual(true, host.ConnectionState);
        Assert.IsNull(host.ConnectionDetail);
    }

    [TestMethod]
    public async Task Discovery_ListsShellyMdnsInstancesOnly_SortedByName()
    {
        var (_, host) = await CreateInitializedAsync();

        // No Address: the plugin skips its HTTP MQTT-status probe, so the
        // test never touches the network
        host.MdnsServices["_http._tcp"] =
        [
            new MdnsServiceInfo { InstanceName = "shellyrgbw2-B", Port = 80, Properties = new Dictionary<string, string>() },
            new MdnsServiceInfo { InstanceName = "printer-1", Port = 80, Properties = new Dictionary<string, string>() },
            new MdnsServiceInfo { InstanceName = "ShellyRGBW2-A", Port = 80, Properties = new Dictionary<string, string>() },
        ];

        var options = await host.OutputProtocols["SHELLYGEN1COLORRGB"].Protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);

        Assert.IsNotNull(options);
        CollectionAssert.AreEqual(new[] { "ShellyRGBW2-A", "shellyrgbw2-B" }, options.Select(x => x.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "ShellyRGBW2-A", "shellyrgbw2-B" }, options.Select(x => x.Label).ToArray());
        Assert.AreEqual(0, host.MdnsRefreshes.Count, "a plain lookup must not force an mDNS refresh");
    }

    [TestMethod]
    public async Task Discovery_WithRefresh_ForcesAnMdnsRefresh()
    {
        var (_, host) = await CreateInitializedAsync();

        var options = await host.OutputProtocols["SHELLYGEN1COLORRGB"].Protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.IsNotNull(options);
        Assert.AreEqual(0, options.Count);
        CollectionAssert.AreEqual(new[] { "_http._tcp" }, host.MdnsRefreshes.ToArray());
    }
}
