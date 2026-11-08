using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using V12.Core.UI.Paper;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;
using XnaVector4 = Microsoft.Xna.Framework.Vector4;

namespace V12.Monogame.Paper
{
    /// <summary>
    /// Draws a renderer-agnostic <see cref="PaperFrame"/> on MonoGame. It uploads
    /// the frame's shared vertex array plus concatenated index slices and binds one
    /// batch of brush/scissor/texture state per mesh. Viewport textures are resolved
    /// from the frame's viewport descriptors, which the 3D renderer fills in.
    /// </summary>
    public sealed class MonogamePaperFrameRenderer : IDisposable
    {
        private const string EffectResource = "V12.Monogame.Paper.PaperEffect.mgfxo";

        private readonly GraphicsDevice _gd;
        private readonly MonogamePaperTextureRenderer? _defaultTextures;
        private MonogamePaperTextureRenderer? _drawTextures;
        private readonly Effect _effect;
        private readonly Texture2D _white;
        private readonly EffectParameter? _viewportSize;
        private readonly EffectParameter? _tex0;
        private readonly EffectParameter? _fontTex;
        private readonly EffectParameter? _scissorTransform;
        private readonly EffectParameter? _scissorTranslation;
        private readonly EffectParameter? _scissorExt;
        private readonly EffectParameter? _brushTransform;
        private readonly EffectParameter? _brushTranslation;
        private readonly EffectParameter? _brushType;
        private readonly EffectParameter? _brushColor1;
        private readonly EffectParameter? _brushColor2;
        private readonly EffectParameter? _brushParams;
        private readonly EffectParameter? _brushParams2;
        private readonly EffectParameter? _textureTransform;
        private readonly EffectParameter? _textureTranslation;
        private readonly EffectParameter? _atlasTexelSize;
        private readonly EffectParameter? _sdfPxRange;

        private PaperVertex[] _vertices = Array.Empty<PaperVertex>();
        private int[] _indices = Array.Empty<int>();
        private readonly List<DrawState> _draws = new();
        private int _vertexCount;
        private int _indexCount;
        private VertexBuffer? _vb;
        private IndexBuffer? _ib;

        private static readonly BlendState PremultipliedBlend = new()
        {
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
            ColorBlendFunction = BlendFunction.Add,
            AlphaBlendFunction = BlendFunction.Add,
        };

        public MonogamePaperFrameRenderer(GraphicsDevice graphicsDevice, MonogamePaperTextureRenderer? textures = null)
        {
            _gd = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
            _defaultTextures = textures;
            _effect = LoadEffect(graphicsDevice);
            _white = new Texture2D(graphicsDevice, 1, 1, false, SurfaceFormat.Color);
            _white.SetData(new[] { XnaColor.White });

            _viewportSize = _effect.Parameters["ViewportSize"];
            _tex0 = _effect.Parameters["tex0"];
            _fontTex = _effect.Parameters["fontTex"];
            _scissorTransform = _effect.Parameters["scissorTransform"];
            _scissorTranslation = _effect.Parameters["scissorTranslation"];
            _scissorExt = _effect.Parameters["scissorExt"];
            _brushTransform = _effect.Parameters["brushTransform"];
            _brushTranslation = _effect.Parameters["brushTranslation"];
            _brushType = _effect.Parameters["brushType"];
            _brushColor1 = _effect.Parameters["brushColor1"];
            _brushColor2 = _effect.Parameters["brushColor2"];
            _brushParams = _effect.Parameters["brushParams"];
            _brushParams2 = _effect.Parameters["brushParams2"];
            _textureTransform = _effect.Parameters["textureTransform"];
            _textureTranslation = _effect.Parameters["textureTranslation"];
            _atlasTexelSize = _effect.Parameters["atlasTexelSize"];
            _sdfPxRange = _effect.Parameters["sdfPxRange"];
        }

        private static Effect LoadEffect(GraphicsDevice graphicsDevice)
        {
            var assembly = typeof(MonogamePaperFrameRenderer).Assembly;
            using var stream = assembly.GetManifestResourceStream(EffectResource)
                ?? throw new InvalidOperationException($"Missing embedded paper effect '{EffectResource}'.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return new Effect(graphicsDevice, memory.ToArray());
        }

        public void Draw(PaperFrame? frame, MonogamePaperTextureRenderer? textures = null)
        {
            if (frame == null || frame.Meshes.Count == 0) return;
            _drawTextures = textures ?? _defaultTextures;
            if (!BuildBuffers(frame)) return;

            int vertexCount = _vertexCount;
            int indexCount = _indexCount;

            if (_vb == null || _vb.VertexCount < vertexCount)
            {
                _vb?.Dispose();
                _vb = new VertexBuffer(_gd, PaperVertex.VertexDeclaration, Math.Max(1024, vertexCount), BufferUsage.WriteOnly);
            }
            if (_ib == null || _ib.IndexCount < indexCount)
            {
                _ib?.Dispose();
                _ib = new IndexBuffer(_gd, IndexElementSize.ThirtyTwoBits, Math.Max(1536, indexCount), BufferUsage.WriteOnly);
            }

            _vb.SetData(_vertices, 0, vertexCount);
            _ib.SetData(_indices, 0, indexCount);

            _gd.SetVertexBuffer(_vb);
            _gd.Indices = _ib;
            _gd.BlendState = PremultipliedBlend;
            _gd.DepthStencilState = DepthStencilState.None;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.SamplerStates[0] = SamplerState.LinearClamp;
            _gd.SamplerStates[1] = SamplerState.LinearClamp;

            float scale = frame.FramebufferScale <= 0f ? 1f : frame.FramebufferScale;
            _viewportSize?.SetValue(new XnaVector2(frame.Width * scale, frame.Height * scale));

            SetDefaultUniforms();

            foreach (var d in _draws)
            {
                _tex0?.SetValue(d.Texture ?? _white);
                _fontTex?.SetValue(d.FontTexture ?? _white);

                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawIndexedPrimitives(
                        PrimitiveType.TriangleList,
                        0,
                        d.Start,
                        d.Count / 3);
                }
            }

            _gd.SetVertexBuffer(null);
        }

        /// <summary>
        /// PaperMesh is the simplified bridge: brush/scissor/texture matrices
        /// do not travel per mesh. Feed identity state so the shared effect
        /// still draws the base vertex colors, texture and font atlas.
        /// </summary>
        private void SetDefaultUniforms()
        {
            _scissorTransform?.SetValue(new XnaVector4(1f, 0f, 0f, 1f));
            _scissorTranslation?.SetValue(XnaVector2.Zero);
            _scissorExt?.SetValue(new XnaVector2(-1f, -1f));
            _brushTransform?.SetValue(new XnaVector4(1f, 0f, 0f, 1f));
            _brushTranslation?.SetValue(XnaVector2.Zero);
            _brushType?.SetValue(0);
            _brushColor1?.SetValue(XnaVector4.One);
            _brushColor2?.SetValue(XnaVector4.One);
            _brushParams?.SetValue(XnaVector4.Zero);
            _brushParams2?.SetValue(XnaVector2.Zero);
            _textureTransform?.SetValue(new XnaVector4(1f, 0f, 0f, 1f));
            _textureTranslation?.SetValue(XnaVector2.Zero);
            _atlasTexelSize?.SetValue(new XnaVector2(1f, 1f));
            _sdfPxRange?.SetValue(4f);
        }

        private bool BuildBuffers(PaperFrame frame)
        {
            var first = frame.Meshes[0];
            int vertexCount = first.VertexCount > 0
                ? Math.Min(first.VertexCount, first.Positions.Length)
                : first.Positions.Length;
            if (vertexCount <= 0) return false;

            if (_vertices.Length < vertexCount) _vertices = new PaperVertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var c = first.Colors[i];
                _vertices[i] = new PaperVertex(
                    ToVector2(first.Positions[i]),
                    ToVector2(first.UVs[i]),
                    new XnaColor(c.R, c.G, c.B, c.A));
            }

            int totalIndices = 0;
            for (int i = 0; i < frame.Meshes.Count; i++) totalIndices += frame.Meshes[i].Indices.Length;
            if (_indices.Length < totalIndices) _indices = new int[Math.Max(totalIndices, 1)];

            _draws.Clear();
            int indexOffset = 0;
            for (int i = 0; i < frame.Meshes.Count; i++)
            {
                var mesh = frame.Meshes[i];
                int count = mesh.Indices.Length;
                if (count <= 0) continue;

                for (int j = 0; j < count; j++) _indices[indexOffset + j] = (int)mesh.Indices[j];

                _draws.Add(new DrawState
                {
                    Start = indexOffset,
                    Count = count,
                    Texture = ResolveTexture(mesh),
                    FontTexture = ResolveFontTexture(mesh),
                });

                indexOffset += count;
            }

            _vertexCount = vertexCount;
            _indexCount = indexOffset;
            return _draws.Count > 0;
        }

        private Texture2D? ResolveTexture(PaperMesh mesh)
            => _drawTextures != null && _drawTextures.TryGetTexture(mesh.TextureId, out var texture)
                ? texture
                : null;

        private Texture2D? ResolveFontTexture(PaperMesh mesh)
            => _drawTextures != null && _drawTextures.TryGetTexture(mesh.FontAtlasId, out var texture)
                ? texture
                : null;

        private static XnaVector2 ToVector2(System.Numerics.Vector2 v) => new(v.X, v.Y);

        public void Dispose()
        {
            _vb?.Dispose();
            _ib?.Dispose();
            _effect.Dispose();
            _white.Dispose();
        }

        private sealed class DrawState
        {
            public int Start;
            public int Count;
            public Texture2D? Texture;
            public Texture2D? FontTexture;
        }

        private struct PaperVertex : IVertexType
        {
            public XnaVector2 Position;
            public XnaVector2 TextureCoordinate;
            public XnaColor Color;

            public PaperVertex(XnaVector2 position, XnaVector2 uv, XnaColor color)
            {
                Position = position;
                TextureCoordinate = uv;
                Color = color;
            }

            public static readonly VertexDeclaration VertexDeclaration = new(
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
                new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
                new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0));

            VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
        }
    }
}
