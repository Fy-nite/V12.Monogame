using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;

namespace V12.Monogame
{
    /// <summary>
    /// A V12 <see cref="IRenderer"/> backed by raw MonoGame. It consumes the
    /// <see cref="RenderPacket"/> V12 pushes from <c>GameRoot.V12Tick</c> and draws its
    /// meshes as lit 3D with <see cref="BasicEffect"/>. Draw happens in the host's
    /// <c>Draw</c> call (<see cref="DrawFrame"/>), because MonoGame owns the device there.
    /// </summary>
    public sealed class MonogameV12Renderer : IRenderer
    {
        private sealed class  GpuMesh
        {
            public VertexBuffer VertexBuffer = null!;
            public IndexBuffer IndexBuffer = null!;
            public int VertexCount;
            public int PrimitiveCount;
            public long Signature;

            public void Dispose()
            {
                VertexBuffer?.Dispose();
                IndexBuffer?.Dispose();
            }
        }

        private readonly GraphicsDevice _gd;
        private readonly GameRoot _root;
        private readonly BasicEffect _effect;
        private readonly Dictionary<IMeshRenderable, GpuMesh> _cache = new();
        private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();

        private RenderPacket? _packet;
        private FrameSnapshot? _snapshot;

        private bool _lockMouse;
        private int _framesSinceFps;
        private int _fps;
        private bool _loggedFirstFrame;
        private string _lastCameraName = "(none)";

        public MonogameV12Renderer(GraphicsDevice graphicsDevice, GameRoot root)
        {
            _gd = graphicsDevice;
            _root = root;
            _effect = new BasicEffect(graphicsDevice)
            {
                TextureEnabled = false,
                VertexColorEnabled = true,
                LightingEnabled = true,
                PreferPerPixelLighting = true,
            };
            _effect.EnableDefaultLighting();
            _effect.DiffuseColor = Vector3.One;
            _effect.SpecularColor = Vector3.Zero;
            _fpsWatch.Start();
        }

        // ── IRenderer ────────────────────────────────────────────────────────

        public bool LockMouse
        {
            get => _lockMouse;
            set => _lockMouse = value;
        }

        public int GetFPS() => _fps;

        public RendererInfo GetAllInfo() => new RendererInfo();

        public int GetScreenWidth() => _gd.Viewport.Width;

        public int GetScreenHeight() => _gd.Viewport.Height;

        public void QueueItems(RenderPacket packet) => _packet = packet;

        public void RemoveItems(RenderPacket packet)
        {
            if (packet == null) return;
            foreach (var draw in packet.Meshes)
            {
                if (draw.Mesh != null && _cache.TryGetValue(draw.Mesh, out var gpu))
                {
                    gpu.Dispose();
                    _cache.Remove(draw.Mesh);
                }
            }
        }

        [Obsolete("use QueueItems instead of QueueItem for better performance")]
        public void QueueItem(IRenderable item)
        {
            if (item is not IMeshRenderable mesh) return;
            _packet ??= new RenderPacket();
            _packet.Meshes.Add(new MeshDraw { Transform = mesh.Transform, Mesh = mesh });
        }

        [Obsolete("use RemoveItems instead of RemoveItem for better performance")]
        public void RemoveItem(IRenderable item)
        {
            if (item is not IMeshRenderable mesh) return;
            if (_cache.TryGetValue(mesh, out var gpu))
            {
                gpu.Dispose();
                _cache.Remove(mesh);
            }
        }

        public void step()
        {
            // No-op: the actual GPU work happens in DrawFrame, called from the host's
            // MonoGame Draw() override where the device is in a valid drawing state.
        }

        public void ApplySnapshot(FrameSnapshot snapshot) => _snapshot = snapshot;

        // ── Drawing ──────────────────────────────────────────────────────────

        public void DrawFrame()
        {
            UpdateFps();

            _gd.Clear(Color.CornflowerBlue);
            _gd.DepthStencilState = DepthStencilState.Default;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.BlendState = BlendState.Opaque;

            var packet = _packet;
            if (packet == null || packet.Meshes.Count == 0) return;

            ResolveCamera(out var view, out var projection);

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Console.WriteLine($"[MonogameRenderer] first frame: {packet.Meshes.Count} mesh(es), camera '{_lastCameraName}'");
            }

            _effect.View = view;
            _effect.Projection = projection;
            _effect.World = Matrix.Identity;
            _effect.LightingEnabled = true;
            _effect.EnableDefaultLighting();
            _effect.PreferPerPixelLighting = true;
            _effect.VertexColorEnabled = true;
            _effect.TextureEnabled = false;
            _effect.DiffuseColor = Vector3.One;

            foreach (var draw in packet.Meshes)
            {
                var mesh = draw.Mesh;
                if (mesh == null) continue;

                var gpu = GetOrBuildGpuMesh(mesh);
                if (gpu == null) continue;

                _effect.World = V12MonogameMath.ToXna(ResolveTransform(mesh));
                _gd.SetVertexBuffer(gpu.VertexBuffer);
                _gd.Indices = gpu.IndexBuffer;

                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawIndexedPrimitives(
                        PrimitiveType.TriangleList,
                        baseVertex: 0,
                        startIndex: 0,
                        primitiveCount: gpu.PrimitiveCount);
                }
            }
        }

        private void UpdateFps()
        {
            _framesSinceFps++;
            if (_fpsWatch.ElapsedMilliseconds < 1000) return;
            _fps = (int)(_framesSinceFps * 1000L / Math.Max(1, _fpsWatch.ElapsedMilliseconds));
            _framesSinceFps = 0;
            _fpsWatch.Restart();
        }

        /// <summary>
        /// Resolve the active camera. Prefers a <see cref="SnapshotNodeType.Camera"/> node
        /// from the last <see cref="ApplySnapshot"/> whose <c>IsCurrentCamera</c> is set;
        /// otherwise finds the current <see cref="ICameraRenderable"/> via GameRoot.
        /// Uses the owning element's world transform (the camera component's own transform
        /// is local-only for a child of the Player).
        /// </summary>
        private void ResolveCamera(out Matrix view, out Matrix projection)
        {
            float aspect = _gd.Viewport.Height > 0 ? (float)_gd.Viewport.Width / _gd.Viewport.Height : 16f / 9f;

            var snapshot = _snapshot;
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Renderables.Count; i++)
                {
                    var rs = snapshot.Renderables[i];
                    if (rs.NodeType != SnapshotNodeType.Camera || !rs.IsCurrentCamera) continue;

                    _lastCameraName = string.IsNullOrEmpty(rs.Name) ? "(snapshot)" : rs.Name;
                    BuildViewProjection(V12MonogameMath.ToXna(rs.Transform), rs.Fov, rs.NearClip, rs.FarClip, aspect, out view, out projection);
                    return;
                }
            }

            var elements = _root.FindElementsWithComponent<ICameraRenderable>();
            IWorldElement? cameraElement = null;
            ICameraRenderable? camera = null;
            foreach (var el in elements)
            {
                var cam = el.GetComponent<ICameraRenderable>();
                if (cam == null) continue;
                if (cam.IsCurrent) { cameraElement = el; camera = cam; break; }
                if (cameraElement == null) { cameraElement = el; camera = cam; }
            }

            if (cameraElement == null || camera == null)
            {
                _lastCameraName = "(fallback)";
                view = Matrix.CreateLookAt(new Vector3(0, 2, 6), new Vector3(0, 1, 0), Vector3.Up);
                projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(80f), aspect, 0.05f, 5000f);
                return;
            }

            _lastCameraName = cameraElement.Name ?? "(unnamed)";
            BuildViewProjection(
                V12MonogameMath.ToXna(cameraElement.WorldTransform),
                camera.FieldOfView, camera.NearClip, camera.FarClip, aspect,
                out view, out projection);
        }

        private static void BuildViewProjection(Matrix world, float fovDegrees, float near, float far, float aspect, out Matrix view, out Matrix projection)
        {
            var position = new Vector3(world.M41, world.M42, world.M43);
            var forward = new Vector3(-world.M31, -world.M32, -world.M33);
            var up = new Vector3(world.M21, world.M22, world.M23);

            if (forward.LengthSquared() < 1e-6f) forward = -Vector3.UnitZ;
            if (up.LengthSquared() < 1e-6f) up = Vector3.Up;
            forward.Normalize();
            up.Normalize();

            view = Matrix.CreateLookAt(position, position + forward, up);
            projection = Matrix.CreatePerspectiveFieldOfView(
                MathHelper.ToRadians(fovDegrees <= 0f ? 75f : fovDegrees),
                aspect,
                near <= 0f ? 0.05f : near,
                far <= near ? near + 1000f : far);
        }

        /// <summary>
        /// Resolve the transform to use for a mesh. <see cref="MeshRenderer.Transform"/>
        /// drops the mesh's Width/Height/Depth, so unwrap to the underlying
        /// <see cref="MeshComponent"/> whose transform applies them.
        /// </summary>
        private static System.Numerics.Matrix4x4 ResolveTransform(IMeshRenderable mesh)
        {
            if (mesh is MeshRenderer renderer && renderer.Mesh is MeshComponent wrapped)
                return wrapped.Transform;
            if (mesh is MeshComponent component)
                return component.Transform;
            return mesh.Transform;
        }

        private GpuMesh? GetOrBuildGpuMesh(IMeshRenderable mesh)
        {
            var points = mesh.MeshPoints;
            var indices = mesh.Indices;
            if (points == null || indices == null || points.Length < 9 || indices.Length < 3)
                return null;

            var color = ResolveColor(mesh);
            long signature = ((long)points.Length << 32) ^ indices.Length ^ ((long)color.PackedValue << 1);

            if (_cache.TryGetValue(mesh, out var existing))
            {
                if (existing.Signature == signature) return existing;
                existing.Dispose();
                _cache.Remove(mesh);
            }

            int vertexCount = points.Length / 3;
            int primitiveCount = indices.Length / 3;

            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= vertexCount)
                    return null; // malformed index data — skip rather than crash the device
            }

            var vertices = new VertexPositionNormalColor[vertexCount];
            var normals = new System.Numerics.Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int p = i * 3;
                vertices[i] = new VertexPositionNormalColor(
                    new Vector3((float)points[p], (float)points[p + 1], (float)points[p + 2]),
                    Vector3.Zero,
                    color);
            }

            for (int t = 0; t < primitiveCount; t++)
            {
                uint i0 = indices[t * 3];
                uint i1 = indices[t * 3 + 1];
                uint i2 = indices[t * 3 + 2];

                var p0 = vertices[i0].Position;
                var p1 = vertices[i1].Position;
                var p2 = vertices[i2].Position;
                var normal = System.Numerics.Vector3.Cross(
                    new System.Numerics.Vector3(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z),
                    new System.Numerics.Vector3(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z));

                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
            }

            for (int i = 0; i < vertexCount; i++)
            {
                var n = normals[i];
                var normal = n.LengthSquared() > 1e-8f
                    ? Vector3.Normalize(new Vector3(n.X, n.Y, n.Z))
                    : Vector3.Up;
                vertices[i].Normal = normal;
            }

            var vertexBuffer = new VertexBuffer(_gd, VertexPositionNormalColor.VertexDeclaration, vertexCount, BufferUsage.WriteOnly);
            vertexBuffer.SetData(vertices);

            var indexBuffer = new IndexBuffer(_gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
            indexBuffer.SetData(indices);

            var gpu = new GpuMesh
            {
                VertexBuffer = vertexBuffer,
                IndexBuffer = indexBuffer,
                VertexCount = vertexCount,
                PrimitiveCount = primitiveCount,
                Signature = signature,
            };
            _cache[mesh] = gpu;
            return gpu;
        }

        private static Color ResolveColor(IMeshRenderable mesh)
        {
            if (mesh is ComponentBase component && component.Owner != null)
            {
                var material = component.Owner.GetComponent<MaterialComponent>();
                if (material != null)
                    return new Color(material.R, material.G, material.B, material.A);
            }
            return new Color(0.8f, 0.8f, 0.8f, 1f);
        }
    }
}
