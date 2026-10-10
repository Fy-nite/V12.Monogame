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
        public static void Run(Action<GameRoot> configureServices, MonogameNetworkOptions? network = null)
        {
            using var game = new V12Game(configureServices, network);
            game.Run();
        }
    }

    /// <summary>
    /// Multiplayer wiring for the MonoGame host (mirrors RootLoop/V12Runtime arg
    /// semantics): no <c>ConnectHost</c> (or <c>ForceServer</c>) runs a server
    /// listening on <c>Port</c>; with <c>ConnectHost</c> the host registers a
    /// client for it (the in-game UI connects on demand — same as Nova).
    /// Null (default) keeps the historical offline behavior for sample hosts.
    /// </summary>
    public sealed class MonogameNetworkOptions
    {
        public int Port = 7777;
        public string? ConnectHost;
        public bool ForceServer;
    }
}
