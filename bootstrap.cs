using System.Numerics;
using System.Reflection;
using V12.Basic;
using V12.Basic.Building;
using V12.Basic.Components;
using V12.Basic.Scene;
using V12.Bindings;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.UI;
using V12.Pak;
using V12.WorldML;

namespace V12.SampleGame
{
    public class Bootstrap : IGameService
    {
        GameRoot _gameroot = default!;
        readonly List<V12PakLoadResult> _pakLoaders = new();
        string? _currentGame;
        IUIBuilder? _ui;
        IWorldElement? _menuEl;
        IWorldElement? _ingameBar;

        public Bootstrap() { }

        public string ReadResource(string name)
        {
            using (Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    Console.WriteLine($"Resource '{name}' not found.");
                    return "";
                }
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        public void Initialize(GameRoot g)
        {
            _gameroot = g;

            Console.WriteLine("Bootstrap.Initialize called");
            BasicRegistry.RegisterAll(_gameroot);

            // Contract (.ct) as a runtime language: register the extension → runtime
            // mapping (.ct → Contract) and compile any component types under
            // scripts/components. Must run before element scripts attach.
            V12ScriptRuntimeRegistration.RegisterAll(_gameroot);
            ContractComponentRegistry.CreateAndRegister(_gameroot);

            var xrService = g.Registry.Get("XRTrackingService");
            if (xrService != null)
            {
                Console.WriteLine("[Bootstrap] XR mode — spawning XR player");
                //SpawnPhysicsWorldOnly();
                //SpawnXrScene(); // no need for XR now
                //SpawnPortalPair();
            }
            else
            {
                Console.WriteLine("[Bootstrap] Desktop mode — spawning physics test world");
                //SpawnPhysicsTestWorld();
            }

            try
            {
                Console.WriteLine("Loading World");
                //var world = WorldLoader.LoadFromArchive("tbg.V12World");
                //_gameroot.SelectedWorld?.Root.AddRange(world.Root);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception loading world: {ex}"); Console.WriteLine(ex.Message);
            }

            Console.WriteLine($"Bootstrap: SelectedWorld is null? {_gameroot.SelectedWorld == null}");

            //LoadContentPaks();
            //BindDemoScene();
            BuildHud();
            BuildGameMenu();
            AutoLaunchFromEnv();
        }

        /// <summary>Unload the active game pak and its gamepaks before switching games.</summary>
        private void UnloadCurrentGame()
        {
            if (_pakLoaders.Count > 0)
            {
                foreach (var loader in _pakLoaders)
                {
                    foreach (var world in loader.LoadedWorlds)
                        _gameroot.Worlds.Remove(world);
                    loader.Dispose();
                }
                _pakLoaders.Clear();
            }
            _gameroot.Gamepaks.Clear();
            _currentGame = null;
            HideIngameBar();
        }

        /// <summary>
        /// Demonstrates Contract (.ct) as a runtime language:
        /// one box driven by a .ct *component type* ("Spin"), and one box running a
        /// .ct *element script* — both living in the running game.
        /// </summary>
        private void SpawnContractDemo()
        {
            var world = _gameroot.SelectedWorld;
            if (world == null)
            {
                Console.WriteLine("[Bootstrap] no selected world — skipping Contract demo");
                return;
            }

            // .ct component type (scripts/components/Spin.ct)
            var spinner = world.SpawnBox(new Vector3(0f, 1.5f, -3f), 1f, 1f, 1f, dynamic: false, name: "SpinBox");
            var registry = ContractComponentRegistry.FromGameRoot();
            if (registry != null && registry.Attach(spinner, "Spin"))
                Console.WriteLine("[Bootstrap] attached .ct component 'Spin' to SpinBox");
            else
                Console.WriteLine("[Bootstrap] Contract component 'Spin' unavailable");

            // .ct element script (scripts/SpinElement.ct) — ScriptComponent dispatches by extension
            var scripted = world.SpawnBox(new Vector3(2f, 1.5f, -3f), 0.6f, 0.6f, 0.6f, dynamic: false, name: "ScriptBox");
            scripted.AddComponent(new ScriptComponent { Source = "scripts/SpinElement.ct" });
        }

        /// <summary>
        /// A simple centred main menu listing every <c>.v12pak</c> found under
        /// <c>games/</c>. Clicking a button loads the pak as the active game:
        /// its worlds are added, its Contract entry scripts' <c>Main</c> runs,
        /// and <c>OnUpdate</c> ticks from then on.
        /// </summary>
        private void BuildGameMenu()
        {
            if (_gameroot.Registry.Get("UIBuilder")?.ServiceInstance is not IUIBuilder ui) return;
            _ui = ui;

            // Auto-sized so Gum can centre the whole block (a fixed size with default size
            // units anchors against the wrong bounds).
            var menu = ui.VLayout(ui.Root, "MainMenu", spacing: 14f, padding: 20f);
            menu.AddComponent(new V12.Components.UI.UIStyleComponent { Anchor = "center" });
            _menuEl = menu;

            ui.Label(menu, "MenuTitle", "V12 Launcher");
            ui.Label(menu, "MenuSubtitle", "Select a game from the games folder");

            var games = DiscoverGames();
            if (games.Count == 0)
                ui.Label(menu, "MenuEmpty", $"No .v12pak found under '{GamesDirectory()}'");

            foreach (var pak in games)
            {
                string name = Path.GetFileNameWithoutExtension(pak);
                ui.Button(menu, name, () => LaunchGame(pak));
            }

            Console.WriteLine($"[Bootstrap] game menu built ({games.Count} game(s))");
        }

        /// <summary>Detach the main menu from the UI tree (stops it rendering and updating).</summary>
        private void HideMenu()
        {
            if (_menuEl != null && _menuEl.Parent != null)
                _menuEl.Parent.RemoveChild(_menuEl);
        }

        /// <summary>Re-attach the main menu to the UI tree.</summary>
        private void ShowMenu()
        {
            if (_menuEl != null && _ui != null && _menuEl.Parent == null)
                _ui.Root.AddChild(_menuEl);
        }

        /// <summary>Small bar shown while a game is running ("← Games" button).</summary>
        private void ShowIngameBar()
        {
            if (_ui == null || _ingameBar != null) return;
            var bar = _ui.VLayout(_ui.Root, "IngameBar", spacing: 6f, padding: 10f);
            _ui.Button(bar, "← Games", BackToMenu);
            _ingameBar = bar;
        }

        private void HideIngameBar()
        {
            if (_ingameBar != null && _ingameBar.Parent != null)
                _ingameBar.Parent.RemoveChild(_ingameBar);
            _ingameBar = null;
        }

        /// <summary>Unload the running game and bring the main menu back.</summary>
        private void BackToMenu()
        {
            UnloadCurrentGame();
            HideIngameBar();
            ShowMenu();
            Console.WriteLine("[Launcher] back to menu");
        }

        /// <summary>Path of the discoverable games folder (next to the exe or cwd).</summary>
        private static string GamesDirectory()
        {
            string besideExe = Path.Combine(AppContext.BaseDirectory, "games");
            if (Directory.Exists(besideExe)) return besideExe;
            return Path.Combine(Directory.GetCurrentDirectory(), "games");
        }

        /// <summary>All <c>.v12pak</c> files in the games folder, ordered by name.</summary>
        private static List<string> DiscoverGames()
        {
            string dir = GamesDirectory();
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.v12pak").OrderBy(p => p).ToList()
                : new List<string>();
        }

        /// <summary>
        /// Auto-launch a game when <c>V12_AUTOGAME</c> matches its pak name
        /// (useful for quick play-tests without clicking the menu).
        /// </summary>
        private void AutoLaunchFromEnv()
        {
            var want = Environment.GetEnvironmentVariable("V12_AUTOGAME");
            if (string.IsNullOrWhiteSpace(want)) return;

            foreach (var pak in DiscoverGames())
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(pak), want, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Bootstrap] V12_AUTOGAME='{want}' — launching");
                    LaunchGame(pak);
                    return;
                }
            }
            Console.WriteLine($"[Bootstrap] V12_AUTOGAME='{want}' did not match any game in {GamesDirectory()}");
        }

        /// <summary>
        /// Load a <c>.v12pak</c> as the active game: its worlds are added to the
        /// game root, the first one is selected, and its Contract gamepaks are
        /// initialized and started. The loader stays alive for as long as the
        /// game runs.
        /// </summary>
        private void LaunchGame(string pakPath)
        {
            try
            {
                Console.WriteLine($"[Launcher] loading game pak '{pakPath}'");
                if (string.Equals(_currentGame, pakPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Launcher] '{pakPath}' is already the active game — ignoring (spurious click?)");
                    return;
                }
                UnloadCurrentGame();
                var loader = _gameroot.LoadPak(pakPath, new V12PakOptions
                {
                    LoadDlls = false,             // Contract scripts only — keep it user-safe
                    LoadContractScripts = true,    // compile & run the game's .ct entrypoints
                    AllowWorldLoading = true,
                });
                _pakLoaders.Add(loader);

                foreach (var world in loader.LoadedWorlds)
                    Console.WriteLine($"[Launcher] loaded world '{world.WorldName}'");

                if (loader.LoadedWorlds.Count > 0)
                {
                    var first = loader.LoadedWorlds[0];
                    _gameroot.SelectWorld(first);
                    Console.WriteLine($"[Launcher] selected world '{first.WorldName}'");
                }

                _gameroot.Gamepaks.InitializeAll();
                _gameroot.Gamepaks.StartAll();
                _currentGame = pakPath;
                Console.WriteLine($"[Launcher] game started ({loader.LoadedGamepaks} gamepak(s))");
                HideMenu();
                ShowIngameBar();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Launcher] failed to load '{pakPath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a small screen-space HUD through the registered UIBuilder. The UIBuilder root
        /// (a CanvasComponent) is captured by GameRoot.CaptureUI and handed to the IUIRenderer
        /// (Gum) — proving the V12 → UI-renderer hand-off and the click → V12 callback loop.
        /// </summary>
        private void BuildHud()
        {
            if (_gameroot.Registry.Get("UIBuilder")?.ServiceInstance is not IUIBuilder ui) return;

            var canvas = ui.Root.GetComponent<V12.Components.UI.CanvasComponent>();
            if (canvas != null)
            {
                canvas.ScreenSpace = true; // first pass renders screen-space canvases
            }
            
            var layout = ui.VLayout(ui.Root, "HudLayout", spacing: 8f, padding: 12f);
            ui.Label(layout, "HudTitle", "V12 + Gum HUD");

            int clicks = 0;
            var btn = ui.Button(layout, "HudButton", () =>
            {
                clicks++;
                Console.WriteLine($"[HUD] button clicked x{clicks}");
                _gameroot.SelectedWorld?.SpawnBox(new Vector3(0f, 4f, -4f), 0.5f, 0.5f, 0.5f, dynamic: true, name: $"HudBox_{clicks}");
            });

            // A widget anchored to the centre of its parent (the canvas) — see UIStyleComponent.Anchor.
            //var centered = ui.Label(ui.Root, "HudCenter", "CENTERED");
            btn.AddComponent(new V12.Components.UI.UIStyleComponent { Anchor = "center" });

            Console.WriteLine("[Bootstrap] HUD built via UIBuilder (canvas captured for Gum)");
        }

        /// <summary>
        /// Loads a <c>.v12pak</c> if one is present (from the V12_PAK env var, or
        /// content.v12pak beside the executable). Pak worlds land on <see cref="GameRoot.Worlds"/>;
        /// the returned loaders are kept alive so the pak's assets remain mounted.
        /// </summary>
        private void LoadContentPaks()
        {
            var pakPath = Environment.GetEnvironmentVariable("V12_PAK")
                          ?? Path.Combine(AppContext.BaseDirectory, "content.v12pak");

            if (!File.Exists(pakPath))
            {
                Console.WriteLine($"[Bootstrap] No pak loaded (looked for '{pakPath}'). Set V12_PAK or drop a content.v12pak.");
                return;
            }

            try
            {
                var result = _gameroot.LoadPak(pakPath, new V12PakOptions { LoadDlls = true, LoadContractScripts = true });
                _pakLoaders.Add(result);
                Console.WriteLine($"[Bootstrap] Loaded pak '{Path.GetFileName(pakPath)}' ({result.LoadedWorlds.Count} world(s)).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bootstrap] Pak load failed: {ex.Message}");
            }
        }

        ///// <summary>
        ///// Loads the embedded WorldML demo scene, merges its elements into the active world, then
        ///// binds C# handlers to its named/tagged elements — the editor-authored-scene → code loop.
        ///// </summary>
        //private void BindDemoScene()
        //{
        //    var world = _gameroot.SelectedWorld;
        //    if (world == null) return;

        //    try
        //    {
        //        var demo = _gameroot.LoadSceneFromResource(Assembly.GetExecutingAssembly(), "V12.SampleGame.DemoLevel.xml", worldName: "DemoLevel", select: false);
        //        var container = demo.Root[0];
        //        foreach (var child in container.Children.ToArray())
        //        {
        //            container.RemoveChild(child);
        //            world.AddElement(child);
        //        }

        //        // WorldML writes TransformComponents; reconcile LocalTransform so physics
        //        // bodies are created at the scene-placed positions.
        //        world.SyncLocalTransforms();
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine($"[Bootstrap] Demo scene unavailable: {ex.Message}");
        //        return;
        //    }

        //    world.Bind()
        //        .On("DemoButton", e => Console.WriteLine($"[Bind] found '{e.Name}' ({e.Components.Count} components)"))
        //        .OnTag("enemy", e =>
        //        {
        //            e.AddComponent(new HealthComponent(50));
        //            Console.WriteLine($"[Bind] enemy '{e.Name}' given 50 HP");
        //        })
        //        .On<TagComponent>("DemoButton", t => Console.WriteLine($"[Bind] DemoButton tags: [{t.Tags}]"))
        //        .On<ButtonComponent>("DemoButton", b =>
        //        {
        //            b.OnPressed = () =>
        //            {
        //                Console.WriteLine($"[Button] '{b.Label}' clicked — spawning a box");
        //                world.SpawnBox(new Vector3(0f, 4f, -4f), 0.5f, 0.5f, 0.5f, dynamic: true, name: "ButtonSpawn");
        //            };
        //            Console.WriteLine($"[Bind] wired '{b.Label}'.OnPressed");
        //        })
        //        .OnPath("DemoEnemies/Enemy_A", e => Console.WriteLine($"[Bind] path 'DemoEnemies/Enemy_A' matched '{e.Name}'"))
        //        .Auto();
        //}
        

        //private void SpawnXrScene()
        //{
        //    var world = _gameroot.SelectedWorld;
        //    if (world == null) return;

        //    var xrPlayer = new Element { Name = "Player" };
        //    var xrPlayerComp = new PlayerComponent { IsXrMode = true };
        //    xrPlayer.AddComponent(xrPlayerComp);
        //    xrPlayer.AddComponent(new VRPlayerComponent());
        //    world.AddElement(xrPlayer);

        //    var xrRoot = new Element { Name = "XR_Root" };
        //    xrRoot.AddComponent(new XRRootComponent());
        //    xrPlayer.AddChild(xrRoot);

        //    var xrHead = MakeBox("XR_Head", new Vector3(0.1f, 0.1f, 0.06f));
        //    xrHead.AddComponent(new XRHeadComponent());
        //    xrHead.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.Head });
        //    xrRoot.AddChild(xrHead);

        //    var xrLeft = MakeBox("XR_LeftHand", new Vector3(0.08f, 0.08f, 0.1f));
        //    xrLeft.AddComponent(new XRHandComponent(HandSide.Left));
        //    xrLeft.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.LeftHand });
        //    xrRoot.AddChild(xrLeft);

        //    var xrRight = MakeBox("XR_RightHand", new Vector3(0.08f, 0.08f, 0.1f));
        //    xrRight.AddComponent(new XRHandComponent(HandSide.Right));
        //    xrRight.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.RightHand });
        //    xrRoot.AddChild(xrRight);

        //    // Ground already created by SpawnPhysicsWorldOnly

        //    // ── Demo button: spawns a physics box when pressed ──
        //    var spawnCount = 0;
        //    var button = world.SpawnBox(new Vector3(0f, 1f, -2f), 0.3f, 0.3f, 0.3f, dynamic: false, name: "SpawnBoxButton");
        //    button.AddComponent(new ButtonComponent
        //    {
        //        Label = "Spawn Box",
        //        OnPressed = () =>
        //        {
        //            spawnCount++;
        //            Console.WriteLine($"[Bootstrap] Spawning box #{spawnCount}");
        //            world.SpawnBox(new Vector3(0f, 2.5f, -4f), 0.4f, 0.4f, 0.4f, dynamic: true, name: $"SpawnedBox_{spawnCount}");
        //        }
        //    });
        //}

        //private void SpawnPhysicsTestWorld()
        //{
        //    //SpawnPhysicsWorldOnly();
        //    //SpawnPortalPair();
        //    //SpawnPlayer();
        //}

        //private void SpawnPhysicsWorldOnly()
        //{
        //    var world = _gameroot.SelectedWorld;
        //    if (world == null) return;

        //    // ---- Ground (top surface at y = -0.5) ----
        //    world.AddGround(size: 40f, thickness: 1f, topY: -0.5f);

        //    // ---- Test objects ----
        //    world.SpawnBoxStack(new Vector3(-3f, 0.5f, 0f), 3, 1f, namePrefix: "Box");                    // tower
        //    world.SpawnBox(new Vector3(3f, 0.5f, -2f), dynamic: true, name: "Box_D");                     // loose boxes
        //    world.SpawnBox(new Vector3(4.5f, 0.5f, 1.5f), 0.8f, 0.8f, 0.8f, dynamic: true, name: "Box_E");
        //    world.SpawnBox(new Vector3(6f, 0.35f, 0f), 3f, 0.3f, 4f, name: "Ramp", rotationDegrees: new Vector3(-18f, 0f, 0f)); // tilted ramp
        //    world.SpawnBox(new Vector3(-7f, 1.6f, -4f), 4f, 0.4f, 4f, name: "Platform");                  // platform
        //}

        //private void SpawnPlayer()
        //{
        //    var player = _gameroot.PersistentWorld.SpawnPlayer3D(at: new Vector3(0f, 1.5f, 0f));
        //    //TODO: add this back at somepoint.
        //    //player.AddComponent(new ScriptComponent
        //    //{
        //    //    ScriptText = "function on_init()\n    print(\"Hello from Lua!\")\nend"
        //    //});
        //    Console.WriteLine("[Bootstrap] Player added to PersistentWorld (survives world switches).");
        //}


    

        private static Element MakeBox(string name, Vector3 scale)
        {
            var b = new Element(name);
            b.AddCollider(MeshShape.Box, scale.X, scale.Y, scale.Z);
            b.AddMesh(MeshShape.Box, scale.X, scale.Y, scale.Z);
            return b;
        }

        public void Update(float deltaTime) { }

        public void Update(GameRoot gameRoot)
        {
            _gameroot = gameRoot;
        }
    }
}
