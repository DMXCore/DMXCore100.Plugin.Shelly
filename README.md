# DMX Core 100 — Shelly Plugin

Drives Shelly Gen1 color devices (RGBW2 and similar) from DMX channel data
over MQTT. This is the plugin edition of the Shelly output support that used
to be built into the core: it registers the same protocol ids
(`SHELLYGEN1COLORRGB`, `SHELLYGEN1COLORRGBW`, `SHELLYGEN1COLORRGBWI`), so
existing output configurations keep working unchanged, whether they were
created under the legacy MQTT output type or the plugin's SHELLY output type.

## Configuration

On the Core's Outputs page, add an output of type **SHELLY** (or use a legacy
MQTT output) with:

- **Protocol** — RGB (3 ch), RGBW (4 ch), or RGBW+intensity (5 ch)
- **Destination Address** — the Shelly device id (the `shellies/{id}` MQTT
  topic segment, e.g. `shellyrgbw2-A4CF12F45478`)
- **Channel Offset** — where the device's channels start within the universe

The device's MQTT broker connection (built-in or external) carries the
commands; the plugin shows disconnected while the broker is down.

## Dev loop

```powershell
./deploy-dev.ps1              # build, pack, upload to localhost:8080
```

Requires a DMXCore.PluginSdk package with contract 1.2+ (IPluginHost.Outputs).
Until that is on NuGet.org, pack it locally from the Software repo with a
package version higher than the latest published one:

```powershell
dotnet pack <Software>/src/PluginSdk -p:Version=1.3.0 -o local-feed
```

(`local-feed/` is this repo's dev-only package source; nupkgs in it are not
committed.)
