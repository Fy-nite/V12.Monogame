using NumMatrix = System.Numerics.Matrix4x4;
using NumVector3 = System.Numerics.Vector3;
using NumQuaternion = System.Numerics.Quaternion;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;
using XnaQuaternion = Microsoft.Xna.Framework.Quaternion;
using XnaColor = Microsoft.Xna.Framework.Color;
using DrawingColor = System.Drawing.Color;

namespace V12.Monogame
{
    /// <summary>
    /// Conversions between V12's <c>System.Numerics</c> math and MonoGame's
    /// <c>Microsoft.Xna.Framework</c> math. Both use a row-vector convention, so a
    /// direct field copy is correct for matrices.
    /// </summary>
    public static class V12MonogameMath
    {
        public static XnaMatrix ToXna(NumMatrix m) => new XnaMatrix(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);

        public static XnaVector3 ToXna(NumVector3 v) => new XnaVector3(v.X, v.Y, v.Z);

        public static XnaQuaternion ToXna(NumQuaternion q) => new XnaQuaternion(q.X, q.Y, q.Z, q.W);

        public static XnaColor ToXna(DrawingColor c) => new XnaColor(c.R, c.G, c.B, c.A);
    }
}
