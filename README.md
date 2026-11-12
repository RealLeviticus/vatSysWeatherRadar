# WeatherRadarPlugin

A prototype vatSys rain-radar overlay using Bureau of Meteorology imagery.

## Prototype behaviour

- Adds a **Weather Radar** submenu to vatSys's **Info** menu with controls to show the overlay, show every radar instead of only your vis range, animate recent scans, and pause/resume animation.
- Shows only the rain inside your vis range by default: the same circles vatSys draws in its Vis Centres window (every vis centre, with a radius of your visibility range or your facility's radio range, whichever is smaller). The picture follows the vis centres as they move, once they have stayed put for a few seconds. **Show All Radars** draws rain from every radar instead. If vatSys has no vis range yet, for example before the connect window has set one, every radar is shown.
- Downloads every available BOM 256 km radar image in the background from the moment vatSys loads the plugin, whether or not the overlay is on show, so switching it on only has to draw what is already held (69 radars are listed; BOM removes a radar's image while it is offline). The 256 km images are used because neighbouring radars are often more than 256 km apart: at 128 km about a quarter of Tasmania, including Launceston, falls outside every image.
- Fetches once per five-minute round. BOM asks users of its FTP service to match their downloads to how often a product is updated, and radar images are updated every five minutes. Every radar stamps its scans at minutes ending in 4 and 9 (UTC) and the file reaches BOM's server one to two minutes later, so the plugin fetches 2 minutes 5 seconds after each stamp, plus up to 40 seconds chosen at random per session so that copies of the plugin do not all connect in the same second. A fetch is one file listing and one small image per radar (about 0.2 MB). This carries on for as long as vatSys is open, including while the overlay is hidden: about 2.4 MB an hour. If a fetch finds fewer than half the radars updated it looks once more a minute later, then waits for the next round.
- Merges all radars into one national picture. Every rain pixel from every radar (1 km apart) is placed, using the radar's own gnomonic projection, into one latitude/longitude grid of roughly 2 km cells, and each cell keeps the strongest return any radar saw there. Where radars overlap the rain is therefore drawn once, not once per radar. Neighbouring cells of equal intensity are merged into rectangles of at most about 128 km; merging changes only how many polygons vatSys holds, never which cells are painted.
- Never drops rain to save memory. A frame is limited to 30,000 rectangles; if the 2 km grid needs more, the whole picture is rebuilt on 4 km and then 8 km cells, each still holding the strongest return inside it, so very wet days look blockier rather than incomplete. Only if 8 km cells are still too many are the weakest levels left out, and that is reported in the vatSys error list.
- Uses the same 15 RGB reflectivity colours found in BOM radar images. Stronger levels render after weaker levels.
- Does not smooth, interpolate, outline, suppress, or invent precipitation. Every displayed cell comes directly from a sampled BOM colour level.
- Paints the rain underneath the profile's maps: it is inserted into vatSys's map list after any `Filled` background map and before everything else, so coastlines, airspace, routes, fixes and map labels are drawn over it. Aircraft tracks and labels are drawn by vatSys after all maps and are always on top. The intensity bands are also transparent, adjustable from the menu.
- Positions each image from the corner coordinates in BOM's `.map` file for that product, not from approximate radar positions. BOM's file for Halls Creek is malformed, so that radar uses the typical image square.
- Removes the overlay immediately when toggled off.
- Preloads the scans from the six rounds before the current one and retains the latest seven frames. Frames are lined up by scan time, so a radar that scans more often (Canberra) or has a gap still lands in the right frame. Animation advances every 900 ms and holds the newest scan for an extra interval; no artificial movement or interpolation is generated.
- Uses BOM's rain-only timestamped images (`IDRnnn2.T.yyyymmddhhmm.png`), which carry the scan time in their name and have no place labels painted over the rain. The decoder also still reads the 524×564 "latest" GIF layout.

BOM's own national mosaic is not used, because it is published several minutes later than the individual radar images; the plugin builds its own. Rain far from a radar is less reliable, because the beam is higher and wider there. Individual radar failures do not prevent other radars from updating. A radar that stops producing scans keeps showing its last one until that scan is 20 minutes older than the newest scan from any radar, then is removed until it returns. The same limit applies to the whole picture: if BOM cannot be reached, or stops publishing, for 20 minutes, the rain is cleared, and if the newest scan on the server is already more than 30 minutes old nothing is drawn. A radar BOM has taken offline has no image to show and stays blank. A second look in the same round updates that round's frame, so the animation keeps one frame per five-minute round.

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
├── WeatherRadarPlugin.cs      vatSys entry point and Info menu
├── RadarOverlayService.cs     scheduling, staleness rules, and all vatSys display state
├── BomFtp.cs                  the only code that talks to BOM (anonymous FTP)
├── HostMemory.cs              safeguard against a memory leak in vatSys's map painting
├── RadarImageDecoder.cs       reads the rain pixels out of a BOM image
├── RadarMosaic.cs             merges all radars into one grid and builds rectangles
├── VisArea.cs                 the controller's vis circles, used to limit the picture
├── RadarDefinition.cs         radar products, image geometry and projection
└── WeatherRadarPlugin.csproj
```

## Client stability

A vatSys plugin shares the client's process, and vatSys renders each ASD on a background
thread whose loop catches only `OperationCanceledException`. An exception raised on that
thread terminates the client, so the plugin is built around two rules:

- **Nothing escapes into a vatSys thread.** The MEF constructor, every menu event, both
  timers, and every action marshalled onto the GUI thread are individually guarded. A
  constructor failure leaves the plugin inert and vatSys starting normally.
- **No unbounded recursion.** `StackOverflowException` cannot be caught, so guards are no
  defence against it; it has to be prevented structurally. Delegate-typed helpers avoid
  overload pairs that differ only by return type, because an expression lambda binds to the
  non-void overload and a helper can silently end up calling itself.
- **Shared display state is only mutated while rendering is paused.** `DisplayMaps.Maps`,
  each logical position's map list, `Map.Infills`, and the custom colour registry are all
  read by render threads without synchronisation. The plugin parks every ASD render loop
  using the same handshake vatSys uses internally, and **abandons** the update if a
  suspension cannot be obtained rather than mutating unsynchronised state.

Fifteen maps — one per reflectivity level — are registered once and then reused for the
life of the client. Frame changes swap the polygons behind those maps rather than adding
or removing maps, so vatSys's map list, its Maps menu, and its per-position map lists stay
a fixed size no matter how long the session runs. Memory is additionally bounded by a cap on rectangles per frame; the picture is coarsened to stay under it, and rain is only ever left out as a last resort, which is reported.

vatSys does not dispose plugins, so `Dispose` exists for correctness but is not part of the
normal lifecycle. Switching the toggle off releases the built pictures, which are the bulk of the memory, and keeps the
downloaded scans (about 5 MB for seven rounds on a very wet day) so that switching it back on downloads nothing.

## Testing

1. Build or copy `WeatherRadarPlugin.dll` into the active profile's `Plugins\WeatherRadarPlugin` directory.
2. Restart vatSys and open an ASD window.
3. Select **Info → Weather Radar → Show Weather Radar**. Rain appears only inside your vis circles; tick **Show All Radars** to see every radar.
4. Rain appears at once (about 0.15 seconds in testing) if vatSys has been open for more than about 15 seconds. Sooner than that, it fills in radar by radar as the first fetch completes. The earlier rounds for animation take up to a minute from when vatSys started.
5. Select **Animate Recent Scans**. The six rounds before the current one are fetched in the background after vatSys starts, so animation is normally available straight away.
6. Select **Pause Animation** to freeze the currently displayed genuine observation; clear it to resume.
7. Clear **Show Weather Radar** and confirm the overlay disappears.

Five seconds after vatSys loads the plugin it fetches the current scan from every radar (about 10 seconds) and then the earlier scans for animation (about 390 small files, 1 MB, under a minute), and from then on one round every five minutes. None of this depends on the overlay being on show. If the overlay is switched on while the first fetch is still running, each radar is drawn as its image arrives (the picture is redrawn at most every 0.75 seconds while it fills in). Missing historical images are skipped without preventing other radars or frames from displaying. If BOM cannot be reached when vatSys starts, the earlier scans are fetched with the first round that succeeds.

While the overlay is hidden, a failed fetch is not put in the vatSys error list; the most recent failure is reported when the overlay is switched on and there is nothing to show.

Faults are recorded in the vatSys error list and rate limited per operation, so a BOM
outage repeating every round cannot bury genuine controller errors.

The overlay is supplemental observation data only and must not be used as the sole basis for separation, navigation, thunderstorm avoidance, or operational aviation decisions. Radar coverage can be blocked or unavailable, attenuation can hide precipitation behind intense cells, and echoes can be anomalous or delayed. Controllers must continue using approved weather and operational sources.

## Memory safeguard

vatSys (build 0.4.9305) leaks about 0.5 KB for every filled map polygon each time it repaints its map layer: it creates a Direct2D geometry sink per polygon and never releases it. Ordinary profiles have few filled polygons, so the loss goes unnoticed. This overlay draws around 20,000 rectangles, which made each repaint cost about 10 MB, and repaints happen on every pan, zoom, animation frame and transparency change. Dragging the transparency slider could exhaust vatSys's 4 GB of address space and crash it in about two minutes.

The plugin defends against this in two ways:

- When the overlay is first switched on it turns on SharpDX's `Configuration.EnableReleaseOnFinalizer`, a documented switch that makes SharpDX release such objects when the garbage collector finalises them. In a reproduction of vatSys's paint loop, 150 repaints grew memory to 1,460 MB without the switch; with it, memory stayed near 140 MB over 300 repaints. The switch is restored when vatSys exits. It applies to all of vatSys's Direct2D objects for the rest of the session, which is the one way this plugin changes the host's behaviour.
- A watch on vatSys's memory, checked every 5 seconds and before every redraw the overlay asks for. At 1,800 MB the overlay switches itself off and says why in the vatSys error list. Memory already lost only comes back when vatSys is restarted.

The proper fix is in vatSys: dispose the geometry sink in `ASDControlDX.PaintMap`.

## Where the data comes from, and the rules

Source: Bureau of Meteorology. Every image is fetched from BOM's anonymous FTP service (`ftp://ftp.bom.gov.au/anon/gen/radar/`), never from `www.bom.gov.au`. This matters:

- BOM's [copyright notice](https://www.bom.gov.au/copyright) does not allow "automated or manual techniques to hack, scrape or otherwise extract material from our site", and says BOM may block an IP address for it without notice. A program downloading radar images from the website is that.
- The anonymous FTP service is the route BOM publishes for programs. Its README shows a scripted fetch, and BOM's radar images guide lists that directory as where the radar images are.
- BOM's terms for the FTP service (see its [FTP public products page](https://www.bom.gov.au/catalogue/anon-ftp.shtml)): the material may be used personally or inside your own organisation, must not be passed on to anyone else or used commercially, and anyone wanting to publish it should become a Registered User. The plugin only shows the images to the person whose computer downloaded them. Streaming or otherwise publishing the overlay is a question for BOM, not something this plugin settles.
- BOM's FTP guide asks users to take each product's update frequency into account when downloading, and gives radar images as updating every five minutes, which is how often the plugin fetches.
- The service is free and comes with no continuity guarantee. BOM does withdraw products from it, announced in `Notification_of_file_removal.txt` on the server. BOM's paid Registered User service is the supported route.
