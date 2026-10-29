using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Int2 = Prowl.Vector.Int2;
using IntRect = Prowl.Vector.IntRect;
using V12.Core.UI.Paper;

namespace V12.Monogame.Paper
{
    /// <summary>
    /// Texture provider for Paper/Quill on MonoGame. It only creates and updates
    /// <see cref="Texture2D"/> handles; all UI geometry comes back as a
    /// <see cref="PaperFrame"/> for <see cref="MonogamePaperFrameRenderer"/>.
    /// </summary>
    public sealed class MonogamePaperTextureRenderer : PaperCanvasRenderer
    {
        private readonly GraphicsDevice _gd;
        private readonly List<Texture2D> _owned = new();

        public MonogamePaperTextureRenderer(GraphicsDevice graphicsDevice)
        {
            _gd = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
        }

        public override object CreateTexture(uint width, uint height)
        {
            var texture = new Texture2D(_gd, Math.Max(1, (int)width), Math.Max(1, (int)height), false, SurfaceFormat.Color);
            _owned.Add(texture);
            return texture;
        }

        public override Int2 GetTextureSize(object texture)
        {
            if (texture is Texture2D t) return new Int2(t.Width, t.Height);
            if (texture is PaperViewportTexture vp && vp.Id != 0) return new Int2(1, 1);
            return base.GetTextureSize(texture);
        }

        public override void SetTextureData(object texture, IntRect bounds, byte[] data)
        {
            if (texture is not Texture2D target) { base.SetTextureData(texture, bounds, data); return; }

            int x = bounds.Min.X;
            int y = bounds.Min.Y;
            int w = Math.Max(0, bounds.Max.X - x);
            int h = Math.Max(0, bounds.Max.Y - y);
            if (w <= 0 || h <= 0) return;

            if (data.Length == w * h * 4)
            {
                var colors = new Color[w * h];
                for (int i = 0; i < colors.Length; i++)
                {
                    int o = i * 4;
                    colors[i] = new Color(data[o], data[o + 1], data[o + 2], data[o + 3]);
                }
                target.SetData(0, new Rectangle(x, y, w, h), colors, 0, colors.Length);
                return;
            }

            if (data.Length == target.Width * target.Height * 4)
            {
                target.SetData(data);
                return;
            }

            int pixels = data.Length / 4;
            int size = (int)Math.Sqrt(pixels);
            if (size <= 0 || size * size != pixels) return;
            var patch = new Color[pixels];
            for (int i = 0; i < patch.Length; i++)
            {
                int o = i * 4;
                patch[i] = new Color(data[o], data[o + 1], data[o + 2], data[o + 3]);
            }
            target.SetData(0, new Rectangle(0, 0, size, size), patch, 0, patch.Length);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (var texture in _owned)
                if (!texture.IsDisposed) texture.Dispose();
            _owned.Clear();
        }
    }
}
