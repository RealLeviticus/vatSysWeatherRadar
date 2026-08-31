# WeatherRadarPlugin

A prototype vatSys rain-radar overlay using Bureau of Meteorology imagery.

## Prototype behaviour

- Adds a **Weather Radar** submenu to vatSys's **Tools** menu with controls to show the overlay, animate recent scans, and pause/resume animation.
- Downloads every available BOM 128 km local radar image when enabled (currently 67 feeds).
- Refreshes the image every five minutes while enabled.
- Scans every source pixel into a 2 km display grid, keeps the strongest return in each cell, and renders discrete rectangular BOM-style radar cells. Neighbouring cells of equal intensity are merged into the largest rectangles that fit, horizontally and vertically. Merging changes only how many polygons vatSys holds, never which cells are painted or where their edges fall.
- Uses the same 15 RGB reflectivity colours found in BOM radar images. Stronger levels render after weaker levels so overlapping radars do not hide stronger precipitation.
- Does not smooth, interpolate, outline, suppress, or invent precipitation. Every displayed cell comes directly from a sampled BOM colour level.
- Uses transparent intensity bands so vatSys routes, fixes, labels, maps, and tracks remain visible below the radar.
- Uses the official corner coordinates from each BOM radar `.map` file rather than approximate radar positions.
- Removes the overlay immediately when toggled off.
- Preloads the six most recent timestamped BOM observations and appends the current scan, then retains the latest seven frames. Animation advances every 900 ms and holds the newest scan for an extra interval; no artificial movement or interpolation is generated.
- Decodes both BOM's 524×564 latest-image GIF layout and its offset-free 512×512 timestamped PNG layout used for animation history.

The national radar mosaic is not used. Individual radar failures do not prevent other available radars from updating.

## Requirements

- Windows with .NET Framework 4.8.1 development tools
- vatSys installed (normally at `C:\Program Files (x86)\vatSys`)
- Visual Studio or Build Tools with MSBuild for release packaging

## Build

```powershell
dotnet build .\WeatherRadarPlugin\WeatherRadarPlugin.csproj `
  -p:VatSysDir="C:\Program Files (x86)\vatSys"
```

The default output directory is:

```text
%UserProfile%\Documents\vatSys Files\Profiles\Australia\Plugins\WeatherRadarPlugin\
```

Override it for local validation when required:

```powershell
dotnet build .\WeatherRadarPlugin\WeatherRadarPlugin.csproj `
  -p:VatSysDir="C:\Program Files (x86)\vatSys" `
  -p:VatSysProfilePluginsDir="C:\Path\To\BuildOutput"
```

## Release package

```powershell
.\build-release.ps1
```

This creates a versioned ZIP under `dist` containing `WeatherRadarPlugin.dll` and installation instructions.

## Current structure

```text
WeatherRadarPlugin.sln
WeatherRadarPlugin/
├── WeatherRadarPlugin.cs      vatSys entry point and Tools menu
├── RadarOverlayService.cs     download, scheduling, and all vatSys display state
├── RadarImageDecoder.cs       BOM image sampling and cell merging
├── RadarDefinition.cs         radar products and BOM corner coordinates
└── WeatherRadarPlugin.csproj
```

## Client stability

A vatSys plugin shares the client's process, and vatSys renders each ASD on a background
thread whose loop catches only `OperationCanceledException`. An exception raised on that
thread terminates the client, so the plugin is built around two rules:

- **Nothing escapes into a vatSys thread.** The MEF constructor, every menu event, both
  timers, and every action marshalled onto the GUI thread are individually guarded. A
  constructor failure leaves the plugin inert and vatSys starting normally.
- **Shared display state is only mutated while rendering is paused.** `DisplayMaps.Maps`,
  each logical position's map list, `Map.Infills`, and the custom colour registry are all
  read by render threads without synchronisation. The plugin parks every ASD render loop
  using the same handshake vatSys uses internally, and **abandons** the update if a
  suspension cannot be obtained rather than mutating unsynchronised state.

Fifteen maps — one per reflectivity level — are registered once and then reused for the
life of the client. Frame changes swap the polygons behind those maps rather than adding
or removing maps, so vatSys's map list, its Maps menu, and its per-position map lists stay
a fixed size no matter how long the session runs. Memory is additionally bounded by a cap
on cells per radar and per frame; if the cap is ever reached it is reported rather than
silently dropping precipitation.

vatSys does not dispose plugins, so `Dispose` exists for correctness but is not part of the
normal lifecycle. The overlay releases its frame memory when the toggle is switched off.

## Testing

1. Build or copy `WeatherRadarPlugin.dll` into the active profile's `Plugins\WeatherRadarPlugin` directory.
2. Restart vatSys and open an ASD window.
3. Select **Tools → Weather Radar → Show All Local Radars**.
4. Allow approximately 20–30 seconds for the initial all-radar download and history preload.
5. Select **Animate Recent Scans**. Recent timestamped scans are preloaded when the overlay is enabled, so animation is available immediately after the initial download completes.
6. Select **Pause Animation** to freeze the currently displayed genuine observation; clear it to resume.
7. Clear **Show All Local Radars** and confirm the overlay disappears.

The initial enable operation downloads the current all-radar view plus recent history and may take longer than a normal five-minute refresh. Missing historical images are skipped without preventing other radars or frames from displaying. Disabling the overlay cancels any download still in flight and releases the cached frames.

Faults are recorded in the vatSys error list and rate limited per operation, so a BOM
outage repeating every five minutes cannot bury genuine controller errors.

The overlay is supplemental observation data only and must not be used as the sole basis for separation, navigation, thunderstorm avoidance, or operational aviation decisions. Radar coverage can be blocked or unavailable, attenuation can hide precipitation behind intense cells, and echoes can be anomalous or delayed. Controllers must continue using approved weather and operational sources.

Source: Bureau of Meteorology. Anonymous BOM radar data is intended for personal or internal organisational use and is not to be redistributed or used commercially without the appropriate permission or licence.
