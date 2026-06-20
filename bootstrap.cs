using V12.Core;
using V12.Core.Core.Interfaces;

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
