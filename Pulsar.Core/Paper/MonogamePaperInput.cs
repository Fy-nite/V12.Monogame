using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PPaper = Prowl.PaperUI.Paper;
using PMouse = Prowl.PaperUI.PaperMouseBtn;
using PKey = Prowl.PaperUI.PaperKey;

namespace V12.Monogame.Paper
{
    /// <summary>
    /// Pushes MonoGame pointer/keyboard state into Paper. Paper is immediate mode and
    /// resolves hover/click/focus on the next BeginFrame, so this must run before the
    /// engine ticks the UI renderer.
    /// </summary>
    internal sealed class MonogamePaperInput
    {
        private readonly Queue<char> _text = new();
        private MouseState _previousMouse;
        private KeyboardState _previousKeys;
        private bool _hasPointer;
        private bool _previousActive = true;

        public void QueueText(char character)
        {
            if (character >= ' ') _text.Enqueue(character);
        }

        public void Feed(PPaper paper, bool active)
        {
            var mouse = Mouse.GetState();
            var keys = Keyboard.GetState();

            if (!active)
            {
                foreach (var key in MappedKeys)
                    paper.SetKeyState(key.Paper, false);
                _previousMouse = mouse;
                _previousKeys = keys;
                _previousActive = false;
                _text.Clear();
                return;
            }

            bool moved = !_hasPointer || !_previousActive ||
                         mouse.X != _previousMouse.X || mouse.Y != _previousMouse.Y;
            if (moved)
            {
                paper.SetPointerState(PMouse.Unknown, mouse.X, mouse.Y, false, true);
                _hasPointer = true;
            }

            FeedButton(paper, PMouse.Left, mouse.LeftButton, mouse.X, mouse.Y);
            FeedButton(paper, PMouse.Middle, mouse.MiddleButton, mouse.X, mouse.Y);
            FeedButton(paper, PMouse.Right, mouse.RightButton, mouse.X, mouse.Y);
            FeedButton(paper, PMouse.Button4, mouse.XButton1, mouse.X, mouse.Y);
            FeedButton(paper, PMouse.Button5, mouse.XButton2, mouse.X, mouse.Y);

            int wheel = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
            if (wheel != 0)
            {
                paper.SetPointerWheel(wheel / 120f);
                paper.SetPointerPosition(mouse.X, mouse.Y);
            }
            int wheelX = mouse.HorizontalScrollWheelValue - _previousMouse.HorizontalScrollWheelValue;
            if (wheelX != 0) paper.SetPointerWheelX(wheelX / 120f);

            foreach (var mapping in MappedKeys)
            {
                bool now = keys.IsKeyDown(mapping.Key);
                bool before = _previousKeys.IsKeyDown(mapping.Key);
                if (now != before) paper.SetKeyState(mapping.Paper, now);
            }

            bool wantsText = paper.FocusedElementId != 0 || paper.WantsCaptureKeyboard;
            if (wantsText)
            {
                while (_text.Count > 0) paper.AddInputCharacter(_text.Dequeue().ToString());
            }
            else
            {
                _text.Clear();
            }

            _previousMouse = mouse;
            _previousKeys = keys;
            _previousActive = true;
        }

        private void FeedButton(PPaper paper, PMouse button, ButtonState state, int x, int y)
        {
            bool down = state == ButtonState.Pressed;
            bool before = button switch
            {
                PMouse.Left => _previousMouse.LeftButton == ButtonState.Pressed,
                PMouse.Middle => _previousMouse.MiddleButton == ButtonState.Pressed,
                PMouse.Right => _previousMouse.RightButton == ButtonState.Pressed,
                PMouse.Button4 => _previousMouse.XButton1 == ButtonState.Pressed,
                PMouse.Button5 => _previousMouse.XButton2 == ButtonState.Pressed,
                _ => false,
            };
            if (down != before) paper.SetPointerState(button, x, y, down, false);
        }

        private static readonly (Keys Key, PKey Paper)[] MappedKeys = BuildKeyMap();

        private static (Keys Key, PKey Paper)[] BuildKeyMap()
        {
            var map = new List<(Keys, PKey)>();
            for (int i = 0; i < 26; i++)
                map.Add(((Keys)((int)Keys.A + i), (PKey)((int)PKey.A + i)));

            map.Add((Keys.D0, PKey.Num0));
            map.Add((Keys.D1, PKey.Num1));
            map.Add((Keys.D2, PKey.Num2));
            map.Add((Keys.D3, PKey.Num3));
            map.Add((Keys.D4, PKey.Num4));
            map.Add((Keys.D5, PKey.Num5));
            map.Add((Keys.D6, PKey.Num6));
            map.Add((Keys.D7, PKey.Num7));
            map.Add((Keys.D8, PKey.Num8));
            map.Add((Keys.D9, PKey.Num9));

            map.Add((Keys.F1, PKey.F1));
            map.Add((Keys.F2, PKey.F2));
            map.Add((Keys.F3, PKey.F3));
            map.Add((Keys.F4, PKey.F4));
            map.Add((Keys.F5, PKey.F5));
            map.Add((Keys.F6, PKey.F6));
            map.Add((Keys.F7, PKey.F7));
            map.Add((Keys.F8, PKey.F8));
            map.Add((Keys.F9, PKey.F9));
            map.Add((Keys.F10, PKey.F10));
            map.Add((Keys.F11, PKey.F11));
            map.Add((Keys.F12, PKey.F12));

            map.Add((Keys.Enter, PKey.Enter));
            map.Add((Keys.Escape, PKey.Escape));
            map.Add((Keys.Back, PKey.Backspace));
            map.Add((Keys.Tab, PKey.Tab));
            map.Add((Keys.Space, PKey.Space));
            map.Add((Keys.OemMinus, PKey.Minus));
            map.Add((Keys.OemPlus, PKey.Equals));
            map.Add((Keys.OemOpenBrackets, PKey.LeftBracket));
            map.Add((Keys.OemCloseBrackets, PKey.RightBracket));
            map.Add((Keys.OemBackslash, PKey.Backslash));
            map.Add((Keys.OemSemicolon, PKey.Semicolon));
            map.Add((Keys.OemQuotes, PKey.Apostrophe));
            map.Add((Keys.OemTilde, PKey.Grave));
            map.Add((Keys.OemComma, PKey.Comma));
            map.Add((Keys.OemPeriod, PKey.Period));
            map.Add((Keys.OemQuestion, PKey.Slash));

            map.Add((Keys.Insert, PKey.Insert));
            map.Add((Keys.Home, PKey.Home));
            map.Add((Keys.PageUp, PKey.PageUp));
            map.Add((Keys.Delete, PKey.Delete));
            map.Add((Keys.End, PKey.End));
            map.Add((Keys.PageDown, PKey.PageDown));
            map.Add((Keys.Right, PKey.Right));
            map.Add((Keys.Left, PKey.Left));
            map.Add((Keys.Down, PKey.Down));
            map.Add((Keys.Up, PKey.Up));

            map.Add((Keys.LeftControl, PKey.LeftControl));
            map.Add((Keys.LeftShift, PKey.LeftShift));
            map.Add((Keys.LeftAlt, PKey.LeftAlt));
            map.Add((Keys.LeftWindows, PKey.LeftSuper));
            map.Add((Keys.RightControl, PKey.RightControl));
            map.Add((Keys.RightShift, PKey.RightShift));
            map.Add((Keys.RightAlt, PKey.RightAlt));
            map.Add((Keys.RightWindows, PKey.RightSuper));

            return map.ToArray();
        }
    }
}
