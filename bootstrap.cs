using System.Numerics;
using System.Reflection;
using V12.Basic;
using V12.Basic.Components;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
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

            var xrProvider = g.Registry.Get<IVRInputProvider>();
            if (xrProvider != null)
            {
                Console.WriteLine("[Bootstrap] XR mode — spawning XR player");
                SpawnXrScene();
            }
            else
            {
                Console.WriteLine("[Bootstrap] Desktop mode — spawning physics test world");
                SpawnPhysicsTestWorld();
            }

            Console.WriteLine($"Bootstrap: SelectedWorld is null? {_gameroot.SelectedWorld == null}");
        }

        private void SpawnXrScene()
        {
            var xrPlayer = new Element { Name = "Player" };
            var xrPlayerComp = new PlayerComponent { IsXrMode = true };
            xrPlayer.AddComponent(xrPlayerComp);
            _gameroot.SelectedWorld?.AddElement(xrPlayer);

            var xrHead = MakeBox("XR_Head", new Vector3(0.1f, 0.1f, 0.06f));
            xrHead.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.Head });
            _gameroot.SelectedWorld?.AddElement(xrHead);

            var xrLeft = MakeBox("XR_LeftHand", new Vector3(0.08f, 0.08f, 0.1f));
            xrLeft.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.LeftHand });
            _gameroot.SelectedWorld?.AddElement(xrLeft);

            var xrRight = MakeBox("XR_RightHand", new Vector3(0.08f, 0.08f, 0.1f));
            xrRight.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.RightHand });
            _gameroot.SelectedWorld?.AddElement(xrRight);
        }

        private void SpawnPhysicsTestWorld()
        {
            // ---- Player ----
            var player = new Element
            {
                Name = "Player",
                LocalTransform = new TRS { Position = new Vector3(0, 1.5f, 0) }
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

            // ---- Ground ----
            var ground = new Element
            {
                Name = "Ground",
                LocalTransform = new TRS { Position = new Vector3(0, -1, 0) }
            };
            ground.AddComponent(new ColliderComponent(MeshShape.Box, 40f, 1f, 40f));
            ground.AddComponent(new PhysicsBodyComponent { IsKinematic = true });
            ground.AddComponent(new MeshComponent { Shape = MeshShape.Box, Width = 40f, Height = 1f, Depth = 40f });
            ground.AddComponent(new MeshRenderer());
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

        private void SpawnPhysicsBox(string name, Vector3 position, Vector3 size, bool kinematic = false)
        {
            var e = new Element
            {
                Name = name,
                LocalTransform = new TRS { Position = position }
            };
            e.AddComponent(new ColliderComponent(MeshShape.Box, size.X, size.Y, size.Z));
            e.AddComponent(new PhysicsBodyComponent { IsKinematic = kinematic });
            e.AddComponent(new MeshComponent { Shape = MeshShape.Box, Width = size.X, Height = size.Y, Depth = size.Z });
            e.AddComponent(new MeshRenderer());
            _gameroot.SelectedWorld?.AddElement(e);
        }

        private static Element MakeBox(string name, Vector3 scale)
        {
            var b = new Element { Name = name };
            b.AddComponent(new ColliderComponent { Name = name });
            var mesh = new MeshComponent { Shape = MeshShape.Box };
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
