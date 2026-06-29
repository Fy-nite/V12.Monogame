using System.Numerics;
using System.Reflection;
using V12.Basic;
using V12.Basic.Components;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.WorldML;

namespace V12.SampleGame
{
    public class Bootstrap : IGameService
    {
        GameRoot _gameroot = default!;

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

            var xrService = g.Registry.Get("XRTrackingService");
            if (xrService != null)
            {
                Console.WriteLine("[Bootstrap] XR mode — spawning XR player");
                SpawnPhysicsWorldOnly();
                SpawnXrScene();
            }
            else
            {
                Console.WriteLine("[Bootstrap] Desktop mode — spawning physics test world");
                SpawnPhysicsTestWorld();
            }

            try
            {
                Console.WriteLine("Loading World");
                var world = WorldLoader.LoadFromArchive("tbg.V12World");
                _gameroot.SelectedWorld?.Root.AddRange(world.Root);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception loading world: {ex}"); Console.WriteLine(ex.Message);
            }

            Console.WriteLine($"Bootstrap: SelectedWorld is null? {_gameroot.SelectedWorld == null}");
        }
        

        private void SpawnXrScene()
        {
            var world = _gameroot.SelectedWorld;
            if (world == null) return;

            var xrPlayer = new Element { Name = "Player" };
            var xrPlayerComp = new PlayerComponent { IsXrMode = true };
            xrPlayer.AddComponent(xrPlayerComp);
            xrPlayer.AddComponent(new VRPlayerComponent());
            world.AddElement(xrPlayer);

            var xrRoot = new Element { Name = "XR_Root" };
            xrRoot.AddComponent(new XRRootComponent());
            xrPlayer.AddChild(xrRoot);

            var xrHead = MakeBox("XR_Head", new Vector3(0.1f, 0.1f, 0.06f));
            xrHead.AddComponent(new XRHeadComponent());
            xrHead.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.Head });
            xrRoot.AddChild(xrHead);

            var xrLeft = MakeBox("XR_LeftHand", new Vector3(0.08f, 0.08f, 0.1f));
            xrLeft.AddComponent(new XRHandComponent(HandSide.Left));
            xrLeft.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.LeftHand });
            xrRoot.AddChild(xrLeft);

            var xrRight = MakeBox("XR_RightHand", new Vector3(0.08f, 0.08f, 0.1f));
            xrRight.AddComponent(new XRHandComponent(HandSide.Right));
            xrRight.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.RightHand });
            xrRoot.AddChild(xrRight);

            // Ground already created by SpawnPhysicsWorldOnly

            // ── Demo button: spawns a physics box when pressed ──
            var spawnCount = 0;
            var button = new Element
            {
                Name = "SpawnBoxButton",
                LocalTransform = new TRS { Position = new Vector3(0, 1f, -2), Rotation = Quaternion.Identity, Scale = Vector3.One }
            };
            button.AddComponent(new ColliderComponent(MeshShape.Box, 0.3f, 0.3f, 0.3f));
            button.AddComponent(new PhysicsBodyComponent { IsKinematic = true });
            var buttonMesh = new MeshComponent(MeshShape.Box, 0.3f, 0.3f, 0.3f);
            button.AddComponent(buttonMesh);
            button.AddComponent(new MeshRenderer { Mesh = buttonMesh });
            button.AddComponent(new ButtonComponent
            {
                Label = "Spawn Box",
                OnPressed = () =>
                {
                    spawnCount++;
                    Console.WriteLine($"[Bootstrap] Spawning box #{spawnCount}");
                    var box = new Element
                    {
                        Name = $"SpawnedBox_{spawnCount}",
                        LocalTransform = new TRS { Position = new Vector3(0, 2.5f, -4), Rotation = Quaternion.Identity, Scale = Vector3.One }
                    };
                    box.AddComponent(new ColliderComponent(MeshShape.Box, 0.4f, 0.4f, 0.4f));
                    box.AddComponent(new PhysicsBodyComponent { IsKinematic = false });
                    var boxMesh = new MeshComponent(MeshShape.Box, 0.4f, 0.4f, 0.4f);
                    box.AddComponent(boxMesh);
                    box.AddComponent(new MeshRenderer { Mesh = boxMesh });
                    _gameroot.SelectedWorld?.AddElement(box);
                }
            });
            world.AddElement(button);
        }

        private void SpawnPhysicsTestWorld()
        {
            SpawnPhysicsWorldOnly();
            SpawnPlayer();
        }

        private void SpawnPhysicsWorldOnly()
        {
            // ---- Ground ----
            var ground = new Element
            {
                Name = "Ground",
                LocalTransform = new TRS { Position = new Vector3(0, -1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }
            };
            ground.AddComponent(new ColliderComponent(MeshShape.Box, 40f, 1f, 40f));
            ground.AddComponent(new PhysicsBodyComponent { IsKinematic = true });
            var groundMesh = new MeshComponent { Shape = MeshShape.Box, Width = 40f, Height = 1f, Depth = 40f };
            ground.AddComponent(groundMesh);
            ground.AddComponent(new MeshRenderer { Mesh = groundMesh });
            _gameroot.SelectedWorld?.AddElement(ground);

            // ---- Stacked boxes ----
            SpawnPhysicsBox("Box_A", new Vector3(-2, 0.5f, 0), new Vector3(1, 1, 1));
            SpawnPhysicsBox("Box_B", new Vector3(0, 0.5f, -3), new Vector3(1, 1, 1));
            SpawnPhysicsBox("Box_C", new Vector3(2, 0.5f, 0), new Vector3(1, 1, 1));
            SpawnPhysicsBox("Box_D", new Vector3(0, 1.5f, -3), new Vector3(1, 1, 1));
            SpawnPhysicsBox("Box_E", new Vector3(-4, 0.5f, -2), new Vector3(1.5f, 0.5f, 1.5f));

            // ---- Ramp ----
            SpawnPhysicsBox("Ramp", new Vector3(4, 0f, 0), new Vector3(3f, 0.2f, 2f), kinematic: true);

            // ---- Walls around play area ----
            SpawnPhysicsBox("Wall_N", new Vector3(0, 1, -10), new Vector3(20, 2, 0.5f), kinematic: true);
            SpawnPhysicsBox("Wall_S", new Vector3(0, 1, 10), new Vector3(20, 2, 0.5f), kinematic: true);
            SpawnPhysicsBox("Wall_E", new Vector3(10, 1, 0), new Vector3(0.5f, 2, 20), kinematic: true);
            SpawnPhysicsBox("Wall_W", new Vector3(-10, 1, 0), new Vector3(0.5f, 2, 20), kinematic: true);
        }

        private void SpawnPlayer()
        {
            var player = new Element
            {
                Name = "Player",
                LocalTransform = new TRS { Position = new Vector3(0, 1.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }
            };
            player.AddComponent(new PlayerComponent());
            player.AddComponent(new LocomotionComponent
            {
                MoveSpeed = 5f,
                JumpStrength = 6f,
                Gravity = 20f
            });
            player.AddComponent(new ColliderComponent(MeshShape.Capsule, 0.6f, 1.8f, 0.6f));
            player.AddComponent(new PhysicsBodyComponent { IsKinematic = false });
            player.AddComponent(new ScriptComponent
            {
                ScriptText = "function on_init()\n    print(\"Hello from Lua!\")\nend"
            });
            _gameroot.SelectedWorld?.AddElement(player);
        }

        private void SpawnPhysicsBox(string name, Vector3 position, Vector3 size, bool kinematic = false)
        {
            var e = new Element
            {
                Name = name,
                LocalTransform = new TRS { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One }
            };
            e.AddComponent(new ColliderComponent(MeshShape.Box, size.X, size.Y, size.Z));
            e.AddComponent(new PhysicsBodyComponent { IsKinematic = kinematic });
            var mesh = new MeshComponent { Shape = MeshShape.Box, Width = size.X, Height = size.Y, Depth = size.Z };
            e.AddComponent(mesh);
            e.AddComponent(new MeshRenderer { Mesh = mesh });
            _gameroot.SelectedWorld?.AddElement(e);
        }

        private static Element MakeBox(string name, Vector3 scale)
        {
            var b = new Element { Name = name };
            b.AddComponent(new ColliderComponent(MeshShape.Box, scale.X, scale.Y, scale.Z));
            var mesh = new MeshComponent(MeshShape.Box, scale.X, scale.Y, scale.Z);
            b.AddComponent(mesh);
            b.AddComponent(new MeshRenderer { Mesh = mesh });
            return b;
        }

        public void Update(float deltaTime) { }

        public void Update(GameRoot gameRoot)
        {
            _gameroot = gameRoot;
        }
    }
}
