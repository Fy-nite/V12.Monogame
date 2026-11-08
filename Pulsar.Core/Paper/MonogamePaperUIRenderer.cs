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
    /// MonoGame host for the engine's canvas UI. The core renderer turns the UI
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
            _frameRenderer = new MonogamePaperFrameRenderer(graphicsDevice, _textures);
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
                _frameRenderer.Draw(surface.LastFrame, surface.Renderer as MonogamePaperTextureRenderer);
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
            color = XnaColor.White;
            var frame = _paper.LastPaperFrame;
            if (frame == null) return false;

            foreach (var viewport in frame.Viewports)
            {
                if (viewport.Id != id) continue;
                if (viewport.Rect.Z <= 0f || viewport.Rect.W <= 0f) return false;
                rect = new XnaRectangle(
                    (int)MathF.Round(viewport.Rect.X),
                    (int)MathF.Round(viewport.Rect.Y),
                    (int)MathF.Round(viewport.Rect.Z),
                    (int)MathF.Round(viewport.Rect.W));
                return true;
            }

            return false;
        }

        public bool TryGetViewportAt(Point position, out long id, out XnaRectangle rect)
        {
            id = 0;
            rect = default;
            var frame = _paper.LastPaperFrame;
            if (frame == null) return false;

            int bestArea = int.MaxValue;
            foreach (var viewport in frame.Viewports)
            {
                float w = viewport.Rect.Z;
                float h = viewport.Rect.W;
                if (w <= 0f || h <= 0f) continue;

                float x = viewport.Rect.X;
                float y = viewport.Rect.Y;
                if (position.X < x || position.Y < y ||
                    position.X >= x + w || position.Y >= y + h) continue;

                int area = (int)(w * h);
                if (area >= bestArea) continue;
                bestArea = area;
                id = viewport.Id;
                rect = new XnaRectangle(
                    (int)MathF.Round(x),
                    (int)MathF.Round(y),
                    (int)MathF.Round(w),
                    (int)MathF.Round(h));
            }
            return id != 0;
        }

        /// <summary>Wire the 3D renderer's viewport textures into the Paper UI
        /// viewport nodes. The render targets are owned by the 3D renderer.</summary>
        public void SetViewportTextureProvider(Func<long, Texture2D?> provider)
        {
            _paper.ViewportTextureProvider = id =>
            {
                var texture = provider(id);
                if (texture != null) _textures.RegisterTexture(texture);
                return texture;
            };
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
