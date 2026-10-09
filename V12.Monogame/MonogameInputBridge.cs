using Microsoft.Xna.Framework.Input;
using V12.Basic.Components;
using V12.Core;
using V12.Core.Input;

namespace V12.Monogame
{
    /// <summary>
    /// Forwards MonoGame keyboard/mouse input into V12's <see cref="InputService"/> using
    /// the logical input names the <c>PlayerComponent</c> action map expects, and feeds
    /// raw mouse deltas to the local player for mouse-look.
    ///
    /// Movement: WASD · Look: arrow keys / mouse · Jump: Space · Run: Shift ·
    /// Interact: left mouse / E · Fly toggle: F · Fly up/down: Q/C. Mouse capture
    /// (click to lock, Esc to release) is handled by the host; when captured, mouse
    /// deltas drive mouse-look.
    /// </summary>
    public sealed class MonogameInputBridge
    {
        private readonly GameRoot _root;
        private readonly MonogameV12Renderer _renderer;

        private InputService? _input;
        private PlayerComponent? _player;

        private KeyboardState _previousKeys;
        private MouseState _previousMouse;
        private bool _mouseInitialized;
        private bool _wasLocked;
        private bool _interactWasDown;

        public MonogameInputBridge(GameRoot root, MonogameV12Renderer renderer)
        {
            _root = root;
            _renderer = renderer;
            _input = root.Registry.Get<InputService>();
            _previousKeys = Keyboard.GetState();
        }

        public void Update(float deltaTime, bool windowActive)
        {
            _input ??= _root.Registry.Get<InputService>();
            _player ??= _root.FindComponent<PlayerComponent>();
            if (_input == null) return;

            var keys = Keyboard.GetState();
            var mouse = Mouse.GetState();

            if (!windowActive)
            {
                // Window unfocused: don't drive movement/look, and resync so regaining focus
                // doesn't produce a one-frame jump (from a stale cursor / held key).
                _mouseInitialized = false;
                _interactWasDown = false;
                _previousKeys = keys;
                _previousMouse = mouse;
                return;
            }

            // Axes are sent EVERY frame, including explicit 0 on release. Some engine
            // systems latch axis state and only update on incoming events (e.g.
            // LocomotionSystem), so without the 0 they keep moving after release.
            // Order within an opposing pair matters: the engine handler that assigns
            // unconditionally must be sent before the guarded one (right before left,
            // forward before backward), otherwise a 0 would cancel the held direction.
            SendAxis("move_right", keys.IsKeyDown(Keys.D));
            SendAxis("move_left", keys.IsKeyDown(Keys.A));
            SendAxis("move_forward", keys.IsKeyDown(Keys.W));
            SendAxis("move_backward", keys.IsKeyDown(Keys.S));

            // Keyboard look.
            SendAxis("look_left", keys.IsKeyDown(Keys.Left));
            SendAxis("look_right", keys.IsKeyDown(Keys.Right));
            SendAxis("look_up", keys.IsKeyDown(Keys.Up));
            SendAxis("look_down", keys.IsKeyDown(Keys.Down));

            // Fly.
            SendAxis("fly_up", keys.IsKeyDown(Keys.Q));
            SendAxis("fly_down", keys.IsKeyDown(Keys.C));

            // Buttons (edge-triggered).
            SendButton("jump", Keys.Space, keys);
            SendButton("run", Keys.LeftShift, keys);
            SendButton("fly_toggle", Keys.F, keys);

            // Interact (aim at a ButtonComponent and press): left mouse or E.
            bool interact = keys.IsKeyDown(Keys.E) || mouse.LeftButton == ButtonState.Pressed;
            if (interact != _interactWasDown)
            {
                _input?.SendEvent(new InputEvent
                {
                    Type = interact ? InputEventType.ButtonDown : InputEventType.ButtonUp,
                    Name = "interact",
                });
                _interactWasDown = interact;
            }

            SendMouseLook(mouse);

            _previousKeys = keys;
            _previousMouse = mouse;
        }

        private void SendAxis(string name, bool held)
        {
            _input?.SendEvent(new InputEvent
            {
                Type = InputEventType.Axis,
                Name = name,
                Value = held ? 1f : 0f,
            });
        }

        private void SendButton(string name, Keys key, KeyboardState keys)
        {
            bool now = keys.IsKeyDown(key);
            bool before = _previousKeys.IsKeyDown(key);

            if (now && !before)
                _input?.SendEvent(new InputEvent { Type = InputEventType.ButtonDown, Name = name });
            else if (!now && before)
                _input?.SendEvent(new InputEvent { Type = InputEventType.ButtonUp, Name = name });
        }

        private void SendMouseLook(MouseState mouse)
        {
            // A lock/unlock transition moves the cursor (or releases it), which would
            // otherwise register as one huge delta. Resync the reference point instead.
            if (_renderer.LockMouse != _wasLocked)
            {
                _wasLocked = _renderer.LockMouse;
                _mouseInitialized = false;
            }

            int centerX = _renderer.GetScreenWidth() / 2;
            int centerY = _renderer.GetScreenHeight() / 2;

            if (!_mouseInitialized)
            {
                _mouseInitialized = true;
                if (_renderer.LockMouse)
                    Mouse.SetPosition(centerX, centerY);
                return;
            }

            int deltaX;
            int deltaY;

            if (_renderer.LockMouse)
            {
                deltaX = mouse.X - centerX;
                deltaY = mouse.Y - centerY;
                Mouse.SetPosition(centerX, centerY);
            }
            else
            {
                deltaX = mouse.X - _previousMouse.X;
                deltaY = mouse.Y - _previousMouse.Y;
            }

            if (deltaX != 0 || deltaY != 0)
                _player?.AddMouseDelta(deltaX, deltaY);
        }
    }
}
