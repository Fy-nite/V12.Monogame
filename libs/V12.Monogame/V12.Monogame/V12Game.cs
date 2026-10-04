using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using V12.Core;

namespace V12.Monogame
{
    /// <summary>
    /// MonoGame host for the V12 engine. Owns the <see cref="GameRoot"/>, wires up the
    /// <see cref="MonogameV12Renderer"/> and the input bridge, and drives the engine from
    /// the MonoGame update/draw loop.
    /// </summary>
    public class V12Game : Game
    {
        private readonly GraphicsDeviceManager _graphics;
        private readonly Action<GameRoot> _configureServices;

        private GameRoot? _root;
        private MonogameV12Renderer? _renderer;
        private MonogameInputBridge? _input;

        private MouseState _previousMouse;
        private bool _escapeWasDown;
        private bool _loggedCaptureHint;

        public V12Game(Action<GameRoot> configureServices)
        {
            _configureServices = configureServices;

            _graphics = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = 1280,
                PreferredBackBufferHeight = 720,
            };

            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            Window.AllowUserResizing = true;
            Window.Title = "V12 (MonoGame)";
        }

        public GameRoot Root => _root!;

        protected override void Initialize()
        {
            base.Initialize();

            _root = new GameRoot();

            // Register the renderer before GameRoot.Initialize() — that is where the
            // engine resolves Registry.Get("IRenderer").
            _renderer = new MonogameV12Renderer(GraphicsDevice, _root);
            _root.Registry.Register("IRenderer", _renderer);

            // Host-supplied game services (e.g. the sample Bootstrap).
            _configureServices(_root);

            // Bootstrap writes into SelectedWorld, so a world must exist first.
            _root.CreateWorld("Demo", "empty");
            _root.Initialize();

            _input = new MonogameInputBridge(_root, _renderer);

            Console.WriteLine("[V12Game] initialized (renderer registered)");
        }

        protected override void Update(GameTime gameTime)
        {
            float deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;

            HandleMouseCapture();

            _input?.Update(deltaTime);
            _root?.V12Tick(deltaTime);

            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            _renderer?.DrawFrame();

            base.Draw(gameTime);
        }

        protected override void UnloadContent()
        {
            _root?.Shutdown();
            base.UnloadContent();
        }

        /// <summary>
        /// Click inside the window to capture the mouse (hidden + confined, so looking
        /// around never hits a screen edge); Esc releases it. Movement keys work either way.
        /// </summary>
        private void HandleMouseCapture()
        {
            if (_renderer == null) return;

            var keys = Keyboard.GetState();
            var mouse = Mouse.GetState();

            if (!_loggedCaptureHint)
            {
                _loggedCaptureHint = true;
                Console.WriteLine("[V12Game] Click the window to capture the mouse; Esc to release. WASD to move.");
            }

            // Esc releases capture.
            bool escapeDown = keys.IsKeyDown(Keys.Escape);
            if (escapeDown && !_escapeWasDown && _renderer.LockMouse)
                SetMouseCapture(false);
            _escapeWasDown = escapeDown;

            // Left-click inside the window captures it.
            bool leftDown = mouse.LeftButton == ButtonState.Pressed;
            bool leftWasDown = _previousMouse.LeftButton == ButtonState.Pressed;
            if (leftDown && !leftWasDown && !_renderer.LockMouse
                && GraphicsDevice.Viewport.Bounds.Contains(mouse.X, mouse.Y))
                SetMouseCapture(true);

            _previousMouse = mouse;
        }

        private void SetMouseCapture(bool locked)
        {
            if (_renderer == null) return;
            _renderer.LockMouse = locked;
            IsMouseVisible = !locked;
        }
    }
}
