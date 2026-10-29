using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace V12.Monogame
{
    /// <summary>
    /// Position + normal + colour + UV vertex, used by the V12 renderer. MonoGame ships
    /// <c>VertexPositionColor</c> and <c>VertexPositionNormalTexture</c> but not this
    /// combination, so we declare it ourselves. The UV channel lets textured meshes
    /// (e.g. game blocks with an albedo map) sample their texture; untextured meshes
    /// leave it at zero — the BasicEffect only reads it when TextureEnabled is set.
    /// </summary>
    public struct VertexPositionNormalColor : IVertexType
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Color Color;
        public Vector2 TextureCoordinate;

        public static readonly VertexDeclaration VertexDeclaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Color, VertexElementUsage.Color, 0),
            new VertexElement(28, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

        VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

        public VertexPositionNormalColor(Vector3 position, Vector3 normal, Color color)
        {
            Position = position;
            Normal = normal;
            Color = color;
            TextureCoordinate = Vector2.Zero;
        }

        public VertexPositionNormalColor(Vector3 position, Vector3 normal, Color color, Vector2 textureCoordinate)
        {
            Position = position;
            Normal = normal;
            Color = color;
            TextureCoordinate = textureCoordinate;
        }
    }
}
