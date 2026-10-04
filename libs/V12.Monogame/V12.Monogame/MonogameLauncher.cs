using System;
using V12.Core;

namespace V12.Monogame
{
    /// <summary>
    /// Shared entry point for the GL and DX hosts. Each host calls this from its
    /// <c>Program.Main</c>, passing the game services it wants registered (e.g. the
    /// sample <c>Bootstrap</c>).
    /// </summary>
    public static class MonogameLauncher
    {
        public static void Run(Action<GameRoot> configureServices)
        {
            using var game = new V12Game(configureServices);
            game.Run();
        }
    }
}
