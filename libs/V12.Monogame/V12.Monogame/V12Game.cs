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
        private GumUIRenderer? _uiRenderer;
        private MonogameInputBridge? _input;

        private bool _escapeWasDown;
        private bool _tabWasDown;
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

            // UI renderer (Gum) — registered before Initialize so GameRoot resolves it and
            // hands it the captured UIFrame each dirty frame.
            _uiRenderer = new GumUIRenderer(this, _root);
            _root.Registry.Register("IUIRenderer", _uiRenderer);

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

            // Stop tracking/releasing the mouse when the window loses focus.
            if (!IsActive && _renderer?.LockMouse == true)
                SetMouseCapture(false);

            HandleMouseCapture();

            _input?.Update(deltaTime, IsActive);
            _root?.V12Tick(deltaTime);

            // Drive Gum (pointer/keyboard) after the frame's UI has been handed over.
            _uiRenderer?.Update(gameTime);

            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            _renderer?.DrawFrame();

            // Gum draws the UI as a 2D overlay on top of the 3D pass.
            _uiRenderer?.Draw();

            base.Draw(gameTime);
        }

        protected override void UnloadContent()
        {
            _root?.Shutdown();
            base.UnloadContent();
        }

        /// <summary>
        /// Tab toggles mouse capture (hidden + confined, for looking around); Esc releases it.
        /// Capture is deliberately NOT bound to left-click, so left-click stays free to press
        /// Gum UI controls. Movement keys work either way.
        /// </summary>
        private void HandleMouseCapture()
        {
            if (_renderer == null) return;

            var keys = Keyboard.GetState();

            if (!_loggedCaptureHint)
            {
                _loggedCaptureHint = true;
                Console.WriteLine("[V12Game] Tab toggles mouse capture; Esc releases it. WASD to move.");
            }

            // Esc releases capture.
            bool escapeDown = keys.IsKeyDown(Keys.Escape);
            if (escapeDown && !_escapeWasDown && _renderer.LockMouse)
                SetMouseCapture(false);
            _escapeWasDown = escapeDown;

            // Tab toggles capture.
            bool tabDown = keys.IsKeyDown(Keys.Tab);
            if (tabDown && !_tabWasDown)
                SetMouseCapture(!_renderer.LockMouse);
            _tabWasDown = tabDown;
        }

        private void SetMouseCapture(bool locked)
        {
            if (_renderer == null) return;
            _renderer.LockMouse = locked;
            IsMouseVisible = !locked;
        }
    }
}
