using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PPaper = Prowl.PaperUI.Paper;
using V12.Core.Interfaces;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;

namespace V12.Monogame.Paper
{
    /// <summary>
    /// MonoGame host for the engine's Paper/Origami UI. The core renderer turns the UI
    /// tree into a <see cref="PaperFrame"/> (array meshes + draw state + viewport
    /// descriptors); this host only supplies textures, feeds input, and hands the
    /// packet to <see cref="MonogamePaperFrameRenderer"/> after the 3D pass.
    /// </summary>
    public sealed class MonogamePaperUIRenderer : IUIRenderer, IDisposable
    {
        private readonly GraphicsDevice _gd;
        private readonly MonogamePaperTextureRenderer _textures;
        private readonly MonogamePaperFrameRenderer _frameRenderer;
        private readonly ProwlPaperRenderer _paper;
        private readonly MonogamePaperInput _input = new();
        private readonly Dictionary<long, RenderTarget2D> _worldTargets = new();
        private int _width;
        private int _height;

        public MonogamePaperUIRenderer(GraphicsDevice graphicsDevice, int width, int height)
        {
            _gd = graphicsDevice;
            _textures = new MonogamePaperTextureRenderer(graphicsDevice);
            _frameRenderer = new MonogamePaperFrameRenderer(graphicsDevice);
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _textures.SetViewport(_width, _height);
            _paper = new ProwlPaperRenderer(_textures, _width, _height, 1f);
            _paper.WorldCanvasRendererFactory = (_, _, _) => new MonogamePaperTextureRenderer(graphicsDevice);
        }

        public ProwlPaperRenderer PaperRenderer => _paper;

        public PPaper PaperInstance => _paper.PaperInstance;

        public bool WantsCapturePointer => _paper.PaperInstance.WantsCapturePointer;

        /// <summary>The packet the 3D renderer reads viewports from and the UI pass draws.</summary>
        public PaperFrame? LastFrame => _paper.LastPaperFrame;

        public IReadOnlyDictionary<long, PaperWorldCanvasSystem.Surface>? WorldCanvases =>
            _paper.WorldCanvases?.Surfaces;

        /// <summary>Bakes every world-canvas Paper frame into a render target. Call in the
        /// draw phase before the 3D pass; the targets are then drawn as world quads.</summary>
        public void BakeWorldCanvases()
        {
            var surfaces = _paper.WorldCanvases?.Surfaces;
            if (surfaces == null || surfaces.Count == 0) return;

            List<long>? stale = null;
            foreach (var id in _worldTargets.Keys)
                if (!surfaces.ContainsKey(id)) (stale ??= new List<long>()).Add(id);
            if (stale != null)
            {
                foreach (var id in stale)
                {
                    _worldTargets[id].Dispose();
                    _worldTargets.Remove(id);
                }
            }

            int backbufferWidth = _gd.PresentationParameters.BackBufferWidth;
            int backbufferHeight = _gd.PresentationParameters.BackBufferHeight;

            foreach (var surface in surfaces.Values)
            {
                int width = Math.Max(1, surface.PixelWidth);
                int height = Math.Max(1, surface.PixelHeight);
                var rt = GetOrCreateWorldTarget(surface.Id, width, height);

                _gd.SetRenderTarget(rt);
                _gd.Viewport = new Viewport(0, 0, width, height);
                _gd.Clear(Color.Transparent);
                _frameRenderer.Draw(surface.LastFrame);
                surface.Texture = rt;
            }

            _gd.SetRenderTarget(null);
            _gd.Viewport = new Viewport(0, 0, Math.Max(1, backbufferWidth), Math.Max(1, backbufferHeight));
        }

        private RenderTarget2D GetOrCreateWorldTarget(long id, int width, int height)
        {
            if (_worldTargets.TryGetValue(id, out var rt))
            {
                if (!rt.IsDisposed && rt.Width == width && rt.Height == height) return rt;
                if (!rt.IsDisposed) rt.Dispose();
                _worldTargets.Remove(id);
            }
            rt = new RenderTarget2D(_gd, width, height, false, SurfaceFormat.Color, DepthFormat.None);
            _worldTargets[id] = rt;
            return rt;
        }

        public void ApplyUI(UIFrame frame) => _paper.ApplyUI(frame);

        public void step() => _paper.step();

        public void FeedInput(int width, int height, bool active)
        {
            Resize(width, height);
            _input.Feed(_paper.PaperInstance, active);
        }

        public void QueueText(char character) => _input.QueueText(character);

        public void Draw() => _frameRenderer.Draw(_paper.LastPaperFrame);

        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;
            _paper.Resize(width, height, 1f);
        }

        public bool TryGetViewportRect(long id, out XnaRectangle rect, out XnaColor color)
        {
            rect = default;
            color = default;
            if (!_paper.TryGetViewportRect(id, out var r, out var c)) return false;

            float w = r.Max.X - r.Min.X;
            float h = r.Max.Y - r.Min.Y;
            if (w <= 0f || h <= 0f) return false;

            rect = new XnaRectangle(
                (int)MathF.Round(r.Min.X),
                (int)MathF.Round(r.Min.Y),
                (int)MathF.Round(w),
                (int)MathF.Round(h));
            color = new XnaColor(c.R, c.G, c.B, c.A);
            return true;
        }

        public bool TryGetViewportAt(Point position, out long id, out XnaRectangle rect)
        {
            id = 0;
            rect = default;
            int bestArea = int.MaxValue;
            foreach (var pair in _paper.ViewportRects)
            {
                var r = pair.Value;
                float w = r.Max.X - r.Min.X;
                float h = r.Max.Y - r.Min.Y;
                if (w <= 0f || h <= 0f) continue;
                if (position.X < r.Min.X || position.Y < r.Min.Y ||
                    position.X >= r.Max.X || position.Y >= r.Max.Y) continue;

                int area = (int)(w * h);
                if (area >= bestArea) continue;
                bestArea = area;
                id = pair.Key;
                rect = new XnaRectangle(
                    (int)MathF.Round(r.Min.X),
                    (int)MathF.Round(r.Min.Y),
                    (int)MathF.Round(w),
                    (int)MathF.Round(h));
            }
            return id != 0;
        }

        public void Dispose()
        {
            _paper.WorldCanvases?.Dispose();
            foreach (var rt in _worldTargets.Values)
                if (!rt.IsDisposed) rt.Dispose();
            _worldTargets.Clear();
            _frameRenderer.Dispose();
            _textures.Dispose();
        }
    }
}
