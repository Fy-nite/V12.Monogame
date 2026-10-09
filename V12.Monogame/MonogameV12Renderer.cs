using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI;

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
        private readonly Dictionary<long, RenderTarget2D> _viewportTargets = new();

        // ── Hierarchy mirror ─────────────────────────────────────────────
        // V12 determines placement; this renderer mirrors the element tree
        // from packet.Nodes (element id → parent id + element-local matrix)
        // and composes world matrices itself — the Godot tree accumulates the
        // same way. Meshes add their mesh-local offset on top. Rebuilt every
        // time a packet arrives (packets only arrive on change).
        private readonly Dictionary<long, System.Numerics.Matrix4x4> _mirrorWorlds = new();

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

        public void QueueItems(RenderPacket packet)
        {
            _packet = packet;
            RebuildMirror(packet);
        }

        private void RebuildMirror(RenderPacket packet)
        {
            _mirrorWorlds.Clear();
            var locals = new Dictionary<long, (long parent, System.Numerics.Matrix4x4 local)>(packet.Nodes.Count);
            foreach (var n in packet.Nodes)
                locals[n.ElementId] = (n.ParentId, n.Local);
            var resolving = new HashSet<long>();
            foreach (var n in packet.Nodes)
                _mirrorWorlds[n.ElementId] = ResolveNodeWorld(n.ElementId, locals, resolving);
        }

        private System.Numerics.Matrix4x4 ResolveNodeWorld(long id,
            Dictionary<long, (long parent, System.Numerics.Matrix4x4 local)> locals,
            HashSet<long> resolving)
        {
            if (_mirrorWorlds.TryGetValue(id, out var done)) return done;
            if (!locals.TryGetValue(id, out var node)) return System.Numerics.Matrix4x4.Identity;
            if (!resolving.Add(id)) return node.local; // cycle guard (malformed tree)
            System.Numerics.Matrix4x4 parentWorld = System.Numerics.Matrix4x4.Identity;
            if (node.parent != 0)
                parentWorld = ResolveNodeWorld(node.parent, locals, resolving);
            resolving.Remove(id);
            var world = node.local * parentWorld; // row-vector order: local × parent
            _mirrorWorlds[id] = world;
            return world;
        }

        /// <summary>Fully composed mesh world matrix from the mirror.
        /// Meshes carry no transforms — placement lives only on elements —
        /// so this is the element's world. Falls back to identity when the
        /// element is absent (stale packet).</summary>
        private System.Numerics.Matrix4x4 MeshDrawWorld(MeshDraw draw)
        {
            if (_mirrorWorlds.TryGetValue(draw.ElementId, out var elementWorld))
                return elementWorld;
            return System.Numerics.Matrix4x4.Identity;
        }

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

        // (Single-item pushes bypass the world walk, so the mirror may lack
        // their ancestors: MeshDrawWorld then falls back to the mesh-local
        // matrix for those draws.)
        [Obsolete("use QueueItems instead of QueueItem for better performance")]
        public void QueueItem(IRenderable item)
        {
            if (item is not IMeshRenderable mesh) return;
            _packet ??= new RenderPacket();
            long id = (mesh as ComponentBase)?.Owner?.Id ?? 0;
            _packet.Meshes.Add(new MeshDraw { ElementId = id, Mesh = mesh, ViewportId = 0 });
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

        // ── Editor primitives ────────────────────────────────────────────────
        // The renderer only exposes data and device access. Picking, the orbit
        // camera and the gizmo live in V12 core (ViewportInteractionService),
        // exactly as nova keeps that logic in WorldCanvasSystem rather than
        // Renderer; MonogameViewportHost adapts these members to it.

        /// <summary>Camera matrices used to render <paramref name="viewportId"/>
        /// (same source of truth as drawing, so pick rays line up with pixels).</summary>
        public bool TryGetViewportCamera(long viewportId, out Matrix view, out Matrix proj)
        {
            view = Matrix.Identity;
            proj = Matrix.Identity;
            var gumUi = _root.Registry.Get<GumUIRenderer>();
            if (gumUi == null || !gumUi.TryGetViewportRect(viewportId, out var rect, out _)) return false;
            if (rect.Width <= 0 || rect.Height <= 0) return false;
            ResolveCamera((float)rect.Width / rect.Height, out view, out proj);
            return true;
        }

        /// <summary>Fill <paramref name="into"/> with the meshes drawn in
        /// <paramref name="viewportId"/> this frame (cleared first).</summary>
        public void CollectPickMeshes(long viewportId, List<ViewportPickMesh> into)
        {
            into.Clear();
            var packet = _packet;
            if (packet == null) return;
            for (int i = 0; i < packet.Meshes.Count; i++)
            {
                var draw = packet.Meshes[i];
                if (draw.ViewportId != viewportId) continue;
                var mesh = draw.Mesh;
                if (mesh == null) continue;
                var points = mesh.MeshPoints;
                var indices = mesh.Indices;
                if (points == null || indices == null || points.Length < 9 || indices.Length < 3) continue;
                into.Add(new ViewportPickMesh(
                    world: MeshDrawWorld(draw),
                    points: mesh.MeshPoints,
                    indices: mesh.Indices,
                    owner: (mesh as ComponentBase)?.Owner));

            }
        }

        // ── Drawing ──────────────────────────────────────────────────────────

        public void DrawFrame()
        {
            UpdateFps();

            var packet = _packet;
            if (packet == null || packet.Meshes.Count == 0) return;

            var groups = new Dictionary<long, List<MeshDraw>>();
            foreach (var draw in packet.Meshes)
            {
                if (!groups.TryGetValue(draw.ViewportId, out var list))
                    groups[draw.ViewportId] = list = new List<MeshDraw>();
                list.Add(draw);
            }

            var gumUi = _root.Registry.Get<GumUIRenderer>();

            // Pass 1: viewport groups — render each into its own RenderTarget2D sized
            // to the viewport panel Gum laid out. Gum picks the texture up via
            // <see cref="GetViewportTexture"/>.
            foreach (var group in groups)
            {
                if (group.Key == 0) continue;
                if (gumUi == null || !gumUi.TryGetViewportRect(group.Key, out var vpRect, out var vpColor))
                    continue;
                if (vpRect.Width <= 0 || vpRect.Height <= 0) continue;

                var rt = GetOrCreateViewportTarget(group.Key, vpRect.Width, vpRect.Height);
                _gd.SetRenderTarget(rt);
                _gd.Viewport = new Viewport(0, 0, rt.Width, rt.Height);
                _gd.Clear(vpColor);
                _gd.DepthStencilState = DepthStencilState.Default;
                // Explicit: Gum's SpriteBatch leaves CullCounterClockwise behind,
                // which culls the wrong faces for V12 meshes (inside-out look).
                _gd.RasterizerState = RasterizerState.CullNone;
                _gd.BlendState = BlendState.Opaque;

                float aspect = rt.Height > 0 ? (float)rt.Width / rt.Height : 16f / 9f;
                ResolveCamera(aspect, out var vpView, out var vpProj);
                DrawMeshes(group.Value, vpView, vpProj);

                _gd.SetRenderTarget(null);
                _gd.Viewport = new Viewport(0, 0, _gd.PresentationParameters.BackBufferWidth, _gd.PresentationParameters.BackBufferHeight);
            }

            // Pass 2: main screen meshes (ViewportId == 0).
            _gd.SetRenderTarget(null);
            _gd.Viewport = new Viewport(0, 0, _gd.PresentationParameters.BackBufferWidth, _gd.PresentationParameters.BackBufferHeight);
            // Dark editor backdrop (UI panels are transparent; cornflower would shine through).
            _gd.Clear(new Color(0x1e, 0x1e, 0x24));
            _gd.DepthStencilState = DepthStencilState.Default;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.BlendState = BlendState.Opaque;

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Console.WriteLine($"[MonogameRenderer] first frame: {packet.Meshes.Count} mesh(es), camera '{_lastCameraName}'");
            }

            float screenAspect = _gd.Viewport.Height > 0 ? (float)_gd.Viewport.Width / _gd.Viewport.Height : 16f / 9f;
            ResolveCamera(screenAspect, out var view, out var projection);
            if (groups.TryGetValue(0, out var mainMeshes))
                DrawMeshes(mainMeshes, view, projection);
            // Overlay intentionally not invoked on the main pass: the editor
            // gizmo belongs in the viewport RTs where the scene actually shows.
        }

        private void DrawMeshes(IEnumerable<MeshDraw> meshes, Matrix view, Matrix projection)
        {
            _effect.View = view;
            _effect.Projection = projection;
            _effect.World = Matrix.Identity;
            _effect.LightingEnabled = true;
            _effect.EnableDefaultLighting();
            _effect.PreferPerPixelLighting = true;
            _effect.VertexColorEnabled = true;
            _effect.TextureEnabled = false;
            _effect.DiffuseColor = Vector3.One;

            foreach (var draw in meshes)
            {
                var mesh = draw.Mesh;
                if (mesh == null) continue;

                var gpu = GetOrBuildGpuMesh(mesh);
                if (gpu == null) continue;

                // Per-mesh draw flags from the owner's MaterialComponent:
                // gizmo handles render flat and on top, everything else stays
                // lit and depth-tested. pass.Apply() below picks the change up.
                ResolveDrawFlags(mesh, out bool unlit, out bool noDepth);
                _effect.LightingEnabled = !unlit;
                _gd.DepthStencilState = noDepth ? DepthStencilState.None : DepthStencilState.Default;

                _effect.World = V12MonogameMath.ToXna(MeshDrawWorld(draw));
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

        /// <summary>Render target a viewport group was drawn into last frame (null until first draw).</summary>
        public RenderTarget2D? GetViewportTexture(long viewportId)
        {
            _viewportTargets.TryGetValue(viewportId, out var rt);
            return rt;
        }

        private RenderTarget2D GetOrCreateViewportTarget(long viewportId, int width, int height)
        {
            if (_viewportTargets.TryGetValue(viewportId, out var rt))
            {
                if (rt.Width == width && rt.Height == height) return rt;
                rt.Dispose();
                _viewportTargets.Remove(viewportId);
            }
            rt = new RenderTarget2D(_gd, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
            _viewportTargets[viewportId] = rt;
            return rt;
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
        private void ResolveCamera(float aspect, out Matrix view, out Matrix projection)
        {
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

        private GpuMesh? GetOrBuildGpuMesh(IMeshRenderable mesh)
        {
            var points = mesh.MeshPoints;
            var indices = mesh.Indices;
            if (points == null || indices == null || points.Length < 9 || indices.Length < 3)
                return null;

            var color = ResolveColor(mesh);
            // Identity, not length: mesh data arrays are replaced — never
            // mutated in place — so a new array is new geometry even at equal
            // lengths. A length-only key renders stale geometry forever
            // (e.g. gizmo handles resizing while dollying keep their first
            // frame's vertices). Reference hashes are O(1).
            long signature = ((long)(uint)RuntimeHelpers.GetHashCode(points) << 32)
                ^ (uint)RuntimeHelpers.GetHashCode(indices)
                ^ ((long)color.PackedValue << 1);

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

        private static void ResolveDrawFlags(IMeshRenderable mesh, out bool unlit, out bool noDepthTest)
        {
            unlit = false;
            noDepthTest = false;
            if (mesh is ComponentBase component && component.Owner != null)
            {
                var material = component.Owner.GetComponent<MaterialComponent>();
                if (material != null)
                {
                    unlit = material.Unlit;
                    noDepthTest = material.NoDepthTest;
                }
            }
        }
    }
}
