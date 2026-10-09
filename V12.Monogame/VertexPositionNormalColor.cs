using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace V12.Monogame
{
    /// <summary>
    /// Position + normal + colour vertex, used by the V12 renderer. MonoGame ships
    /// <c>VertexPositionColor</c> and <c>VertexPositionNormalTexture</c> but not this
    /// combination, so we declare it ourselves.
    /// </summary>
    public struct VertexPositionNormalColor : IVertexType
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Color Color;

        public static readonly VertexDeclaration VertexDeclaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Color, VertexElementUsage.Color, 0));

        VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

        public VertexPositionNormalColor(Vector3 position, Vector3 normal, Color color)
        {
            Position = position;
            Normal = normal;
            Color = color;
        }
    }
}
