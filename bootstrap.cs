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
        readonly List<V12PakLoader> _pakLoaders = new();

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
            BuildMainMenu();
            SpawnContractDemo();
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
        /// A simple centred main menu: Start / Settings / Edit. The menu box is a sized
        /// container anchored to the centre of the canvas, and the buttons flow inside it.
        /// (An anchor is relative to the parent, so a button inside the top-left HUD layout
        /// would centre within that layout, not the screen.)
        /// </summary>
        private void BuildMainMenu()
        {
            if (_gameroot.Registry.Get("UIBuilder")?.ServiceInstance is not IUIBuilder ui) return;

            // Auto-sized so Gum can centre the whole block (a fixed size with default size
            // units anchors against the wrong bounds).
            var menu = ui.VLayout(ui.Root, "MainMenu", spacing: 10f, padding: 10f);
            menu.AddComponent(new V12.Components.UI.UIStyleComponent { Anchor = "center" });

            ui.Button(menu, "MenuStart", () => Console.WriteLine("[Menu] Start"));
            ui.Button(menu, "MenuSettings", () => Console.WriteLine("[Menu] Settings"));
            ui.Button(menu, "MenuEdit", () => Console.WriteLine("[Menu] Edit"));

            Console.WriteLine("[Bootstrap] main menu built (centred)");
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
                var loader = _gameroot.LoadPak(pakPath, V12PakOptions.FullTrust);
                _pakLoaders.Add(loader);
                Console.WriteLine($"[Bootstrap] Loaded pak '{Path.GetFileName(pakPath)}' ({loader.Results.Count} step(s)).");
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
