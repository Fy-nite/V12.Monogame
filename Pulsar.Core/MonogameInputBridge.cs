using Microsoft.Xna.Framework.Input;
using V12.Basic.Components;
using V12.Core;
using V12.Core.Input;
using System;

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

        /// <summary>Host hook: true while the UI owns the pointer, so a click does not
        /// also fire the game's interact action.</summary>
        public Func<bool>? UiWantsPointer { get; set; }

        /// <summary>All keyboard keys, cached for raw per-key edge forwarding.</summary>
        private static readonly Keys[] AllKeys = (Keys[])Enum.GetValues(typeof(Keys));

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

            // Raw keyboard: forward every key edge with its typed Key so handlers
            // and scripts can react to any key (e.g. Tab to toggle a menu), not
            // just the named action map above.
            foreach (var key in AllKeys)
            {
                if (!Enum.TryParse<InputKey>(key.ToString(), out var mapped)) continue;
                bool now = keys.IsKeyDown(key);
                if (now == _previousKeys.IsKeyDown(key)) continue;

                _input?.SendEvent(new InputEvent
                {
                    Type = now ? InputEventType.ButtonDown : InputEventType.ButtonUp,
                    Name = key.ToString(),
                    Key = mapped,
                });
            }

            // Interact (aim at a ButtonComponent and press): left mouse or E.
            // Paper owns the pointer while it is over (or dragging) UI, so the
            // same click does not also fire the game's interact action.
            bool leftDown = mouse.LeftButton == ButtonState.Pressed;
            bool uiOwnsPointer = windowActive && (UiWantsPointer?.Invoke() ?? false);
            bool interact = keys.IsKeyDown(Keys.E) || (leftDown && !uiOwnsPointer);
            if (interact != _interactWasDown)
            {
                _input?.SendEvent(new InputEvent
                {
                    Type = interact ? InputEventType.ButtonDown : InputEventType.ButtonUp,
                    Name = "interact",
                });
                _interactWasDown = interact;
            }

            // Raw mouse button edges for scripts (mine/place). Sent
            // separately from the named "interact" action so games
            // can bind left/right click to their own behavior.
            bool rightDown = mouse.RightButton == ButtonState.Pressed;
            if (leftDown && _previousMouse.LeftButton == ButtonState.Released)
                _input?.SendEvent(new InputEvent { Type = InputEventType.ButtonDown, Name = "mouse_left" });
            else if (!leftDown && _previousMouse.LeftButton == ButtonState.Pressed)
                _input?.SendEvent(new InputEvent { Type = InputEventType.ButtonUp, Name = "mouse_left" });

            if (rightDown && _previousMouse.RightButton == ButtonState.Released)
                _input?.SendEvent(new InputEvent { Type = InputEventType.ButtonDown, Name = "mouse_right" });
            else if (!rightDown && _previousMouse.RightButton == ButtonState.Pressed)
                _input?.SendEvent(new InputEvent { Type = InputEventType.ButtonUp, Name = "mouse_right" });

            // Mouse wheel for scripts (hotbar slot selection): forward the
            // per-frame scroll delta as a named axis. MonoGame accumulates
            // 120 units per detent on Windows; the sign is what games use
            // (positive = scrolled up = previous slot).
            int wheelDelta = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
            if (wheelDelta != 0)
                _input?.SendEvent(new InputEvent { Type = InputEventType.Axis, Name = "mouse_wheel", Value = wheelDelta });

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
            {
                // Forward raw mouse deltas through the input service so
                // scripts (and any handler without a PlayerComponent) can
                // drive first-person look themselves. Sent only when the
                // mouse actually moved, as per-frame deltas.
                _input?.SendEvent(new InputEvent
                {
                    Type = InputEventType.Axis,
                    Name = "mouse_dx",
                    Value = deltaX,
                });
                _input?.SendEvent(new InputEvent
                {
                    Type = InputEventType.Axis,
                    Name = "mouse_dy",
                    Value = deltaY,
                });
                _player?.AddMouseDelta(deltaX, deltaY);
            }
        }
    }
}
