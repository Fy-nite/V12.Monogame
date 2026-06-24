using System.Reflection;
using V12.Basic;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.WorldML;
namespace V12.SampleGame
{
    public class Bootstrap : IGameService
    {
        GameRoot _gameroot;
        
        public Bootstrap() { }
        public string ReadResource(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    Console.WriteLine($"Resource '{name}' not found.");
                    return "";
                }
                using (StreamReader reader = new StreamReader(stream))
                {
                    string result = reader.ReadToEnd();
                    return result;
                }
            }
        }

        /// <summary>
        /// Welcome to your game loop, this function init's your codebase to start
        /// </summary>
        /// <param name="g"></param>
        public void Initialize(GameRoot g)
        {
            _gameroot = g;

            Console.WriteLine("Bootstrap.Initialize called");
            V12.Basic.BasicRegistry.RegisterAll(_gameroot);

            var xrProvider = g.Registry.Get<IVRInputProvider>();
            if (xrProvider != null)
            {
                Console.WriteLine("[Bootstrap] XR mode — spawning XR player");
                var xrPlayer = new Element { Name = "Player" };
                var xrPlayerComp = new PlayerComponent { IsXrMode = true };
                xrPlayer.AddComponent(xrPlayerComp);
                _gameroot.SelectedWorld?.AddElement(xrPlayer);

                var xrHead = Procedurals.GenBox("XR_Head", new System.Numerics.Vector3(0.1f, 0.1f, 0.06f));
                xrHead.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.Head });
                _gameroot.SelectedWorld?.AddElement(xrHead);

                var xrLeft = Procedurals.GenBox("XR_LeftHand", new System.Numerics.Vector3(0.08f, 0.08f, 0.1f));
                xrLeft.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.LeftHand });
                _gameroot.SelectedWorld?.AddElement(xrLeft);

                var xrRight = Procedurals.GenBox("XR_RightHand", new System.Numerics.Vector3(0.08f, 0.08f, 0.1f));
                xrRight.AddComponent(new XRVisualizerComponent { Target = XRPoseTarget.RightHand });
                _gameroot.SelectedWorld?.AddElement(xrRight);
            }
            else
            {
                Console.WriteLine("[Bootstrap] Desktop mode — spawning desktop player");
                var player = new Element { Name = "Player" };
                player.AddComponent(new PlayerComponent());
                player.AddComponent(new ScriptComponent() { ScriptText = "function on_init()\r\n    print(\"Hello from Lua!\")\r\nend" });
                _gameroot.SelectedWorld?.AddElement(player);
                _gameroot.SelectedWorld?.AddElement(Procedurals.GenBox("Box", new System.Numerics.Vector3(1, 1, 1)));
            }

            Console.WriteLine($"Bootstrap: SelectedWorld is null? {_gameroot.SelectedWorld == null}");
            try
            {
                var parsedWorld = new WorldMLParser().Parse("""
                    <World name="thingy">
                        <Element name="mmeow">
                     <Component type="ScriptComponent"><![CDATA[
                    function on_init()
                        print("inline lua")
                    end
                    ]]></Component>
                        </Element>
                    </World>
                    """);
                Console.WriteLine($"[Bootstrap] Parsed world '{parsedWorld.Name}' with {(parsedWorld.Children?.Count ?? 0)} children");
                if (parsedWorld.Children != null)
                {
                    foreach (var child in parsedWorld.Children)
                    {
                        Console.WriteLine($"[Bootstrap]   Child: '{child.Name}' ({child.Components?.Count ?? 0} components, {child.Children?.Count ?? 0} children)");
                    }
                }
                _gameroot.SelectedWorld?.AddElement(parsedWorld);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bootstrap] ERROR parsing WorldML: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[Bootstrap] StackTrace: {ex.StackTrace}");
            }
        }

        public void Update(float deltaTime)
        {
          
        }

        public void Update(GameRoot gameRoot)
        {
            _gameroot = gameRoot;
        }
    }
}
