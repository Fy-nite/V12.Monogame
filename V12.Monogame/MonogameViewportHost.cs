using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using V12.Core;
using V12.Core.UI;
using XnaGame = Microsoft.Xna.Framework.Game;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;
using XnaPoint = Microsoft.Xna.Framework.Point;
using XnaRect = Microsoft.Xna.Framework.Rectangle;
using XnaVec3 = Microsoft.Xna.Framework.Vector3;
using XnaViewport = Microsoft.Xna.Framework.Graphics.Viewport;
using XnaVertPosCol = Microsoft.Xna.Framework.Graphics.VertexPositionColor;

namespace V12.Monogame
{
    /// <summary>
    /// MonoGame adapter for the core viewport interaction: exposes the renderer
    /// primitives (viewport hit-testing, camera matrices, pick meshes,
    /// screen-space projection, overlay lines) as <see cref="IViewportInteractionHost"/>
    /// and raw mouse/keyboard state as <see cref="IEditorInputSource"/>, so the
    /// orbit / picking / gizmo logic itself lives in V12 core
    /// (<c>ViewportInteractionService</c>) and stays host-agnostic — nova
    /// provides its own implementation of the same pair.
    /// </summary>
    public sealed class MonogameViewportHost : IViewportInteractionHost, IEditorInputSource
    {
        private readonly XnaGame _game;
        private readonly GumUIRenderer _ui;
        private readonly MonogameV12Renderer _renderer;

        private Func<long, Matrix4x4, Matrix4x4, ViewportLine[]?>? _overlay;

        public MonogameViewportHost(XnaGame game, GumUIRenderer ui, MonogameV12Renderer renderer)
        {
            _game = game;
            _ui = ui;
            _renderer = renderer;
        }

        // ── IEditorInputSource ──────────────────────────────────────────────

        public EditorInputSnapshot GetSnapshot()
        {
            var m = Mouse.GetState();
            var k = Keyboard.GetState();
            return new EditorInputSnapshot
            {
                MousePosition = new Vector2(m.X, m.Y),
                LeftDown = m.LeftButton == ButtonState.Pressed,
                RightDown = m.RightButton == ButtonState.Pressed,
                Wheel = m.ScrollWheelValue,
                KeyT = k.IsKeyDown(Keys.T),
                KeyR = k.IsKeyDown(Keys.R),
                KeyS = k.IsKeyDown(Keys.S),
                KeyEscape = k.IsKeyDown(Keys.Escape),
                WindowActive = _game.IsActive,
                MouseLocked = _renderer.LockMouse,
            };
        }

        // ── IViewportInteractionHost ───────────────────────────────────────────────────

        public bool TryGetViewportAt(Vector2 screenPos, out long viewportId)
            => _ui.TryGetViewportAt(new XnaPoint((int)screenPos.X, (int)screenPos.Y), out viewportId, out _);

        public bool TryGetViewportCamera(long viewportId, out Matrix4x4 view, out Matrix4x4 projection)
        {
            if (_renderer.TryGetViewportCamera(viewportId, out var xv, out var xp))
            {
                view = V12MonogameMath.ToNumerics(xv);
                projection = V12MonogameMath.ToNumerics(xp);
                return true;
            }
            view = default;
            projection = default;
            return false;
        }

        public bool TryUnprojectRay(long viewportId, Vector2 screenPos, out Vector3 origin, out Vector3 direction)
        {
            origin = default;
            direction = default;
            if (!TryGetLocalViewport(viewportId, out var rect)) return false;
            if (!_renderer.TryGetViewportCamera(viewportId, out var view, out var proj)) return false;

            var screen = new XnaViewport(0, 0, rect.Width, rect.Height);
            float sx = screenPos.X - rect.X;
            float sy = screenPos.Y - rect.Y;
            var vwp = view * proj;
            var near = screen.Unproject(new XnaVec3(sx, sy, 0f), vwp, XnaMatrix.Identity, XnaMatrix.Identity);
            var far = screen.Unproject(new XnaVec3(sx, sy, 1f), vwp, XnaMatrix.Identity, XnaMatrix.Identity);
            var delta = far - near;
            if (delta.LengthSquared() < 1e-12f) return false;
            origin = new Vector3(near.X, near.Y, near.Z);
            var n = V12MonogameMath.ToNumerics(delta);
            direction = Vector3.Normalize(n);
            return true;
        }

        public bool TryProjectToScreen(long viewportId, Vector3 worldPosition, out Vector2 screenPosition)
        {
            screenPosition = default;
            if (!TryGetLocalViewport(viewportId, out var rect)) return false;
            if (!_renderer.TryGetViewportCamera(viewportId, out var view, out var proj)) return false;

            var screen = new XnaViewport(0, 0, rect.Width, rect.Height);
            var p = screen.Project(
                new XnaVec3(worldPosition.X, worldPosition.Y, worldPosition.Z),
                view * proj, XnaMatrix.Identity, XnaMatrix.Identity);
            // Window-space: the grab test compares against the raw mouse position.
            screenPosition = new Vector2(p.X + rect.X, p.Y + rect.Y);
            return true;
        }

        public void CollectPickMeshes(long viewportId, List<ViewportPickMesh> into)
            => _renderer.CollectPickMeshes(viewportId, into);

        public Func<long, Matrix4x4, Matrix4x4, ViewportLine[]?>? ViewportOverlay
        {
            get => _overlay;
            set
            {
                _overlay = value;
                _renderer.ViewportOverlay = value == null
                    ? null
                    : (viewportId, xv, xp) =>
                    {
                        var lines = value(viewportId,
                            V12MonogameMath.ToNumerics(xv), V12MonogameMath.ToNumerics(xp));
                        if (lines == null || lines.Length == 0) return null;
                        var verts = new XnaVertPosCol[lines.Length * 2];
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var c = lines[i].Color;
                            var xna = new Microsoft.Xna.Framework.Color(c.R, c.G, c.B, c.A);
                            var a = lines[i].A;
                            var b = lines[i].B;
                            verts[i * 2] = new XnaVertPosCol(new XnaVec3(a.X, a.Y, a.Z), xna);
                            verts[i * 2 + 1] = new XnaVertPosCol(new XnaVec3(b.X, b.Y, b.Z), xna);
                        }
                        return verts;
                    };
            }
        }

        private bool TryGetLocalViewport(long viewportId, out XnaRect rect)
        {
            return _ui.TryGetViewportRect(viewportId, out rect, out _)
                && rect.Width > 0 && rect.Height > 0;
        }
    }
}
