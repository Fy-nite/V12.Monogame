using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Basic;
using V12.Basic.Components;
using V12.Components;
using V12.WorldML;
namespace V12.SampleGame
{
    public class Bootstrap : IGameService
    {
        GameRoot _gameroot;
        
        public Bootstrap() { }
        public void Initialize(GameRoot g)
        {
            _gameroot = g;

            Console.WriteLine("Bootstrap.Initialize called");
            V12.Basic.BasicRegistry.RegisterAll(_gameroot);

            var player = new Element { Name= "Player" };
            player.AddComponent(new PlayerComponent());

            bool added = _gameroot.SelectedWorld != null;
            _gameroot.SelectedWorld?.AddElement(player);
            _gameroot.SelectedWorld?.AddElement(Procedurals.GenBox("Box", new System.Numerics.Vector3(1, 1, 1)));
            Console.WriteLine($"Bootstrap: SelectedWorld is null? {!added}");
            try
            {
                var parsedWorld = new WorldMLParser().Parse("""
               

                <World name="test_audio">
                	
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
