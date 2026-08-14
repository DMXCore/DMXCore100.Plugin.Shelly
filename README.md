# DMX Core 100 — Shelly Plugin

Drives Shelly Gen1 color devices (RGBW2 and similar) from DMX channel data
over MQTT. This is the plugin edition of the Shelly output support that used
to be built into the core: it registers the same protocol ids
(`SHELLYGEN1COLORRGB`, `SHELLYGEN1COLORRGBW`, `SHELLYGEN1COLORRGBWI`), so
existing output configurations keep working unchanged.

It is also deliberately public as the **reference example for building your
own DMX Core 100 plugin** — the whole plugin is one small file, but it
exercises the real machinery: manifest, lifecycle, output-protocol
registration, per-device sessions, MQTT, and the packaging/CI story. This
README doubles as the walkthrough.

## Configuration (as a user)

On the Core's Outputs page, add an output of type **SHELLY** (or use a legacy
MQTT output) with:

- **Protocol** — RGB (3 ch), RGBW (4 ch), or RGBW+intensity (5 ch)
- **Destination Address** — the Shelly device id (the `shellies/{id}` MQTT
  topic segment, e.g. `shellyrgbw2-A4CF12F45478`; it's also the device's
  mDNS name)
- **Start Channel** — the DMX start address of the device's channels within the slot

The device's MQTT broker connection (built-in or external) carries the
commands, and the Shelly itself must have MQTT enabled and pointed at the
same broker. The plugin shows disconnected while the broker is down.

The **Discover** button lists Shelly devices found via mDNS, annotated with
each device's own MQTT state (queried over its local HTTP API) — e.g.
`shellyrgbw2-A4CF12F45478 (192.168.1.30, MQTT → 192.168.1.5:1883)` or
`… (192.168.1.31, MQTT off)` — so a device pointed at the wrong broker (or
none) is visible before you map it.

---

# Anatomy of a DMX Core 100 plugin

## What a plugin is

A `.dmxplugin` file is a **zip archive** containing `manifest.json` plus your
compiled assemblies. The Core extracts it into its plugin folder, loads the
assembly named in the manifest in an isolated load context, finds the one
class implementing `IPlugin`, and calls it. That's the whole deployment
model — no installers, no registration.

```
shelly-plugin.dmxplugin
├── manifest.json
└── DMXCore100.ShellyPlugin.dll
```

The manifest ([src/DMXCore100.ShellyPlugin/manifest.json](src/DMXCore100.ShellyPlugin/manifest.json)):

```json
{
  "id": "shelly",                                  // stable, lowercase; storage/log/config scoping
  "name": "Shelly",
  "version": "1.0.0",
  "entryAssembly": "DMXCore100.ShellyPlugin.dll",  // the DLL containing your IPlugin
  "minSdkVersion": "1.2",                          // lowest SDK contract you need (see Versioning)
  "author": "DMX Core"
}
```

## Project setup

One class library referencing the
[`DMXCore.PluginSdk`](https://www.nuget.org/packages/DMXCore.PluginSdk)
package. Two things matter in the
[csproj](src/DMXCore100.ShellyPlugin/DMXCore100.ShellyPlugin.csproj):

- `ExcludeAssets="runtime"` on the SDK reference — the Core provides the SDK
  at runtime and always redirects to its own copy, so the SDK DLL must not
  ship inside your archive.
- `manifest.json` copied to the output directory, so publishing produces the
  complete archive content.

Everything else is a normal .NET class library. Plugins are full-trust: you
can open sockets, use any NuGet package (those DLLs *do* ship in your
archive), and spin up background work.

## Lifecycle

Implement [`IPlugin`](https://www.nuget.org/packages/DMXCore.PluginSdk) in a
class with a public parameterless constructor:

- `Info` — static identity (`PluginInfo`): id/name/version, plus any
  admin-editable **settings** (rendered as a form on the Core's Plugins page)
  and **triggers** you fire.
- `InitializeAsync(IPluginHost host, ...)` — called once at startup (or on
  hot-reload upload). Register everything here; keep it fast.
- `ShutdownAsync` — dispose what you registered. Every `Register*`/
  `Subscribe*` call returns an `IDisposable`, so the pattern is: collect them
  in a list, dispose the list ([ShellyPlugin.cs](src/DMXCore100.ShellyPlugin/ShellyPlugin.cs)
  does exactly this).

`IPluginHost` is your window into the device: `Logger`, `Settings`, `Mqtt`
(the device's broker connection), `Mdns` (shared mDNS/DNS-SD discovery),
`Entities` (the preset/cue/zone catalog), `Playback`, `Triggers`,
`ControlValues` (DSP backends), `Outputs` (this plugin's bread and butter),
persistent state JSON, and `SchedulePeriodic` for background work. Callbacks
are dispatched serially per plugin and fault-isolated — a throwing handler is
logged and counted, never crashes the Core.

## Output protocols — driving lights from DMX data

The Shelly plugin's core is three calls like this in `InitializeAsync`:

```csharp
host.Outputs.RegisterOutputProtocol(
    new OutputProtocolDescriptor
    {
        Id = "SHELLYGEN1COLORRGBW",          // stored in device config — never change it
        DisplayName = "Shelly Gen1 Color RGBW",
        PortType = "SHELLY",                 // your own entry in the output-type list
        MaxUpdatesPerSecond = 10,            // host-enforced per-device rate limit
    },
    new ShellyOutputProtocol(host, ShellyChannelMode.Rgbw));
```

After this, "SHELLY" shows up as an output type on the Core's Outputs page
next to sACN and Art-Net, and your protocol appears in its Protocol dropdown.
The Core handles universes, merging, fades, rate limiting, and deduping — you
only see channel bytes for devices the user mapped.

Your `IPluginOutputProtocol` answers two questions:

- `GetChannelCount(config)` — how many DMX channels one mapping consumes
  (3 for RGB, pixels × 3 for a pixel device — the mapping's config is
  available if the count depends on it).
- `OpenSessionAsync(config)` — create an `IPluginOutputSession` for one
  mapped device. This is where per-device state lives: a socket, a DTLS
  handshake, or (here) just the precomputed MQTT topic.

The session's `SendAsync(channelValues, ct)` delivery contract is worth
internalizing, because it's what makes plugins safe:

- You are called from a **host worker thread**, never the render loop. A slow
  or blocking send cannot affect the Core's DMX output timing.
- You always get the **latest** values only. Intermediate frames are
  coalesced (latest-wins); there is no backlog to drain.
- The host already deduped (no unchanged sends) and rate-limited
  (`MaxUpdatesPerSecond`).
- Return `false` or throw to signal failure: the host disposes your session,
  backs off, reopens, and retries with the newest values. So "broker down"
  is simply `return false` — see `ShellySession.SendAsync`.
- The memory you receive is only valid during the call — copy it if you need
  it afterward.

## Connection status

Call `host.SetConnectionState(bool, detail)` on every transition of whatever
your plugin depends on — it drives the Plugins page indicator and the
on-device status. The Shelly plugin ties it to the broker connection via
`host.Mqtt.OnConnectionChanged` (which always delivers the current state
first, so startup is race-free).

## Dev loop

```powershell
./deploy-dev.ps1                # build, pack, upload to localhost:8080 (prompts for PIN)
./deploy-dev.ps1 -Server http://192.168.1.50:8080 -Pin 1234
```

The Core hot-reloads the uploaded plugin — no restart. Logs land in the
device's regular logs tagged with your plugin id.

To test logic without a device, use the
[`DMXCore.PluginSdk.Testing`](https://www.nuget.org/packages/DMXCore.PluginSdk.Testing)
package: `TestPluginHost` is an in-memory host that records what your plugin
does (MQTT publishes, registrations) and lets you simulate host events and
deliver output values deterministically.

## Packaging and CI

`pack.ps1` / `pack.sh` publish the project and zip the output — that zip *is*
the `.dmxplugin`. The [GitHub Actions workflow](.github/workflows/build.yml)
packs on every push and maintains a rolling `latest` release, which is how
the DMX Core product builds bundle first-party plugins. For your own plugin,
users simply upload the `.dmxplugin` on the Plugins page.

Gotcha for Windows-created repos: shell scripts need the execute bit in git
(`git update-index --chmod=+x pack.sh`) or Linux CI fails with exit code 126.

## Versioning

Two different version axes, easy to conflate:

- **SDK contract** (`minSdkVersion` in the manifest) — compared against the
  SDK's AssemblyVersion major.minor on the device. Declare the lowest
  contract that has everything you use (`1.2` = output protocols). Devices
  with an older SDK refuse to load the plugin instead of crashing.
- **NuGet package version** of `DMXCore.PluginSdk` — runs *ahead* of the
  contract version; just reference `Version="1.*"` and declare the contract
  you need in the manifest.

Your own plugin's version (manifest + `PluginInfo`) is yours; bump it on
every release so upgrades apply cleanly.
