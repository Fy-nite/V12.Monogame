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

            Console.WriteLine("Bootstrap.Initialize called");
            V12.Basic.BasicRegistry.RegisterAll(_gameroot);

            var player = new Element { Name= "Player" };
            player.AddComponent(new PlayerComponent());

            bool added = _gameroot.SelectedWorld != null;
            _gameroot.SelectedWorld?.AddElement(player);
            _gameroot.SelectedWorld?.AddElement(Procedurals.GenBox("Box", new System.Numerics.Vector3(1, 1, 1)));
            Console.WriteLine($"Bootstrap: SelectedWorld is null? {!added}");
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
