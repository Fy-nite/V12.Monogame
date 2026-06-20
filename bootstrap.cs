using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Basic;
using V12.Basic.Components;
using V12.Components;
namespace V12.SampleGame
{
    public class Bootstrap : IGameService
    {
        GameRoot _gameroot;
        
        public Bootstrap() { }
        public void Initialize(GameRoot g)
        {
            _gameroot = g;

            Console.WriteLine("Welcome to the game engine");
            V12.Basic.BasicRegistry.RegisterAll(_gameroot);

            _gameroot.CreateWorld("MainWorld");

            var player = new Element { Name= "Player" };
            //player.AddComponent(new TransformComponent { X = 0, Y = 0, Z = 0 });
            player.AddComponent(new PlayerComponent());

            _gameroot.SelectedWorld?.AddElement(player);
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
