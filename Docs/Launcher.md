# Game Launcher & `.v12pak` Games

The sample is now a **launcher**: it scans a `games/` folder for `.v12pak`
archives, lists them as buttons in the main menu, and loads the chosen pak as
the active game — its WorldML worlds are added to the running `GameRoot`, its
Contract entry script's `Main` runs, and `OnUpdate` ticks every frame.

## Folder layout

```
<repo>/
  games/                 # discoverable games (copied to the build output)
    SpinWorld.v12pak
  games_src/             # authoring source, packed with V12PakBuilder
    SpinWorld/
      worlds/SpinWorld/world.xml
      scripts/Main.ct
  Scramble.csproj        # references libs/V12.basic/libs/V12.Pak (Contract-enabled)
```

At runtime the launcher looks for `games/` next to the executable first and
then under the current working directory.

## The launch flow (`bootstrap.cs`)

1. `BuildGameMenu()` scans `games/*.v12pak` and adds one button per pak.
2. Clicking a button (or setting `V12_AUTOGAME=<pakname>`) calls `LaunchGame(pakPath)`.
3. `LaunchGame` loads the pak with Contract enabled:

```csharp
var loader = _gameroot.LoadPak(pakPath, new V12PakOptions
{
    LoadDlls = false,             // keep user games safe: no native code
    LoadContractScripts = true,   // compile & run the game's .ct entrypoints
    AllowWorldLoading = true,
});
```

4. The first `loader.LoadedWorlds` entry is made active via
   `_gameroot.SelectWorld(...)`.
5. `_gameroot.Gamepaks.InitializeAll()` then `.StartAll()` run — for a
   `ContractGamepack` this compiles `scripts/Main.ct`, runs `Main`, and
   registers a per-frame tick that invokes `OnUpdate(dt)`.
6. `_pakLoaders.Add(loader)` keeps the extracted temp dir alive; switching
   games disposes the previous loader, removes its worlds, and clears
   `Gamepaks` before starting the new one. Re-clicking a spurious repeat is
   ignored (`_currentGame` guard).

## Writing a game

A game is a folder mirroring the pak layout, packed into `*.v12pak`:

```
MyGame/
  manifest.json            # optional — generated when absent
  worlds/MyWorld/world.xml # WorldML scene (also templates/ for templates)
  assets/**                # meshes, textures, audio, fonts
  scripts/Main.ct          # Contract gamepak entrypoint
```

`manifest.json` (when present) declares the parts:

```json
{
  "version": 1,
  "engine": "v12",
  "worlds":  [{ "name": "MyWorld", "entry": "worlds/MyWorld/world.xml", "templatesDir": "worlds/MyWorld/templates/" }],
  "assets":  { "textures/crate.png": "assets/textures/crate.png" },
  "paks":    [],
  "scripts": ["scripts/Main.ct"]
}
```

Every `scripts/*.ct` becomes a `ContractGamepack` and its module runs as a
small program: `Main` once, `OnUpdate(dt)` every frame.

```ct
import __builtin.std;

Contract Program {
    static angle: float;
    static cubeId: long;

    static fn Main() {
        cubeId = V12.World.SpawnElementAt("Cube", 0.0, 1.5, -4.0);
        V12.Components.AddMesh(cubeId, 0, 1.0, 1.0, 1.0);   // 0 = Box
        V12.Components.SetColor(cubeId, 1.0, 0.4, 0.2, 1.0);
    }

    static fn OnUpdate(dt: float) {
        angle = angle + 60.0 * dt;
        V12.Components.SetFloat(cubeId, "Transform", "RY", angle * 0.0174533);
    }
}
```

Remember the Contract rule from `Docs/Contract_Scripting.md`:
`IO`, `Math`, … are not global — `import __builtin.std;` at the top.

## Packing a game

Use the `V12PakBuilder`:

```csharp
V12.Pak.V12PakBuilder.Build("games_src/MyGame", "games/MyGame.v12pak");
```

It scans for `worlds/*/world.xml`, `assets/**`, `paks/*.dll`, and
`scripts/*.ct` and generates `manifest.json` when missing.

## Trust model

`V12PakOptions` lets the launcher decide how much code to run from a pak:

| Setting | Effect |
| --- | --- |
| `LoadDlls = false` | skip bundled C# gamepaks |
| `LoadContractScripts = false` | skip compiling `.ct` scripts |
| `AllowedAssetExtensions` | whitelist asset file types |
| `AllowWorldLoading = false` | import assets only, no worlds |

Contract compilation runs arbitrary code just like DLLs, so it sits in the
same trust tier — `LoadDlls` is off and `LoadContractScripts` on in this
launcher only because you're shipping your own games in `games/`.
