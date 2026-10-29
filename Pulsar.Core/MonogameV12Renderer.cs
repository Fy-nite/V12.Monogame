using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using V12.Core.UI.Paper;

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

        // ── Albedo texture cache ────────────────────────────────────────
        // Resolved path -> GPU texture for every mesh albedo map bound at
        // draw time (MaterialComponent.PrimaryTexture). A null value is a
        // negative cache: the file failed to load, so don't retry each
        // frame. Point sampling keeps block pixels crisp.
        private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);

        // ── World-space canvases ───────────────────────────────────────────
        // Baked Paper frames drawn as textured quads at the canvas element's
        // world transform. Width is fixed; height follows the texture aspect.
        private const float WorldCanvasWidthMetres = 2f;
        private readonly BasicEffect _quadEffect;
        private readonly Dictionary<long, (VertexBuffer vb, float w, float h)> _quadCache = new();

        // ── Environment & baked-in lighting ────────────────────────────────
        // Driven by the first EnvironmentComponent / ILightRenderable found in
        // the active worlds (same live-lookup class as the camera: V12 has no
        // env/light packet feed yet). Games that define neither still get a
        // pleasant day sky + sun rig, so lighting works with zero setup.
        private const float SkyDomeRadius = 800f;
        private readonly BasicEffect _skyEffect;
        private VertexBuffer? _skyVb;
        private IndexBuffer? _skyIb;
        private int _skyPrimCount;
        private Vector3 _skyTop, _skyHorizon, _skyBottom;
        private bool _skyBuilt;
        private bool _loggedPanoFallback;
        private bool _loggedLightSkip;
        private bool _loggedLightSanitize;

        private sealed class EnvState
        {
            public bool Skybox = true;
            public Vector3 Sky = new(0.30f, 0.55f, 0.90f);
            public Vector3 Horizon = new(0.75f, 0.85f, 0.95f);
            public Vector3 Ground = new(0.10f, 0.12f, 0.16f);
            public Vector3 Ambient = new(0.38f, 0.40f, 0.46f);
            public Color ClearColor = new(0x12, 0x16, 0x22);
            public readonly Vector3[] SunDirs = new Vector3[3];
            public readonly Vector3[] SunColors = new Vector3[3];
            public int SunCount;
        }

        private static readonly Vector3 DefaultSunDir = Vector3.Normalize(new Vector3(0.45f, -1f, 0.35f));
        private static readonly Vector3 DefaultSunColor = new(1f, 0.95f, 0.85f);
        private static readonly Vector3 DefaultFillDir = Vector3.Normalize(new Vector3(-0.55f, -0.35f, -0.45f));
        private static readonly Vector3 DefaultFillColor = new Vector3(0.45f, 0.55f, 0.70f) * 0.45f;
        private static readonly Vector3 DefaultBounceColor = new Vector3(0.35f, 0.33f, 0.30f) * 0.35f;

        private readonly EnvState _env = new();
        private string _envSig = "";
        private int _lastMeshCount = -1;

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
            _quadEffect = new BasicEffect(graphicsDevice)
            {
                TextureEnabled = true,
                VertexColorEnabled = false,
                LightingEnabled = false,
            };
            _skyEffect = new BasicEffect(graphicsDevice)
            {
                TextureEnabled = false,
                VertexColorEnabled = true,
                LightingEnabled = false,
            };
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
            ResolveEnvironment();
            if (packet.Meshes.Count != _lastMeshCount)
            {
                _lastMeshCount = packet.Meshes.Count;
                Console.WriteLine($"[MonogameRenderer] packet: {packet.Meshes.Count} mesh(es), {packet.Nodes.Count} node(s)");
            }
        }

        /// <summary>
        /// Refresh baked-in environment + lights from the worlds. Runs on packet
        /// arrival (dirty-driven), not per draw: component edits mark dirty, so
        /// tweaking colors/lights in-game updates within a frame.
        /// </summary>
        private void ResolveEnvironment()
        {
            var envEl = _root.FindElementWithComponent<EnvironmentComponent>();
            var env = envEl?.GetComponent<EnvironmentComponent>();

            if (env != null)
            {
                var sky = new Vector3(env.SkyR, env.SkyG, env.SkyB);
                _env.Sky = sky;
                // Gradient convention (documented): horizon lifts the sky color
                // toward white, ground drops the ambient tint dark.
                _env.Horizon = Vector3.Lerp(sky, Vector3.One, 0.45f);
                var amb = new Vector3(env.AmbientR, env.AmbientG, env.AmbientB);
                _env.Ground = amb * 0.6f;
                _env.Ambient = amb * Math.Max(0f, env.AmbientEnergy);
                _env.Skybox = env.Mode == BackgroundMode.Skybox;
                _env.ClearColor = _env.Skybox
                    ? new Color(_env.Horizon.X, _env.Horizon.Y, _env.Horizon.Z, 1f)
                    : new Color(sky.X, sky.Y, sky.Z, 1f);
                if (_env.Skybox && !string.IsNullOrEmpty(env.SkyboxPath) && !_loggedPanoFallback)
                {
                    _loggedPanoFallback = true;
                    Console.WriteLine("[MonogameRenderer] panorama/cubemap skyboxes unsupported; gradient fallback.");
                }
            }
            // else: keep defaults (pleasant day sky + rig, zero game setup).

            int n = 0;
            bool skippedNonDirectional = false;
            bool skippedOverflow = false;
            foreach (var el in _root.FindElementsWithComponent<ILightRenderable>())
            {
                var l = el.GetComponent<ILightRenderable>();
                if (l == null || l.Type != LightType.Directional)
                {
                    if (l != null) skippedNonDirectional = true;
                    continue;
                }
                if (n >= 3) { skippedOverflow = true; continue; }
                // Travel direction: -Z of the owning element's world (engine convention).
                var w = el.WorldTransform;
                var fwd = new Vector3(-w.M31, -w.M32, -w.M33);
                if (fwd.LengthSquared() < 1e-6f) fwd = new Vector3(0f, -1f, 0f);
                fwd.Normalize();
                var c = l.Color;
                _env.SunDirs[n] = fwd;
                _env.SunColors[n] = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f) * Math.Max(0f, l.Intensity);
                n++;
            }
            _env.SunCount = n;
            if ((skippedNonDirectional || skippedOverflow) && !_loggedLightSkip)
            {
                _loggedLightSkip = true;
                Console.WriteLine("[MonogameRenderer] only the first 3 directional lights apply (BasicEffect); point/spot skipped.");
            }

            // Poisoned values (NaN/Inf from a bad sync) would black out every
            // lit mesh while unlit elements keep rendering — fall back per field.
            bool clean = true;
            _env.Sky = Sanitize(_env.Sky, new Vector3(0.30f, 0.55f, 0.90f), ref clean);
            _env.Horizon = Sanitize(_env.Horizon, new Vector3(0.75f, 0.85f, 0.95f), ref clean);
            _env.Ground = Sanitize(_env.Ground, new Vector3(0.10f, 0.12f, 0.16f), ref clean);
            _env.Ambient = Sanitize(_env.Ambient, new Vector3(0.38f, 0.40f, 0.46f), ref clean);
            for (int i = 0; i < _env.SunCount; i++)
            {
                _env.SunDirs[i] = SanitizeDir(_env.SunDirs[i], DefaultSunDir, ref clean);
                _env.SunColors[i] = Sanitize(_env.SunColors[i], DefaultSunColor, ref clean);
            }
            if (!clean && !_loggedLightSanitize)
            {
                _loggedLightSanitize = true;
                Console.WriteLine("[MonogameRenderer] invalid lighting values (NaN/Inf) fell back to defaults.");
            }

            string sig = $"env={env != null} skybox={_env.Skybox} sky=({_env.Sky.X:F2},{_env.Sky.Y:F2},{_env.Sky.Z:F2}) " +
                $"amb=({_env.Ambient.X:F2},{_env.Ambient.Y:F2},{_env.Ambient.Z:F2}) suns={_env.SunCount}" +
                string.Concat(Enumerable.Range(0, _env.SunCount).Select(i =>
                    $" sun{i}=({_env.SunDirs[i].X:F2},{_env.SunDirs[i].Y:F2},{_env.SunDirs[i].Z:F2})x({_env.SunColors[i].X:F2},{_env.SunColors[i].Y:F2},{_env.SunColors[i].Z:F2})"));
            if (sig != _envSig)
            {
                _envSig = sig;
                Console.WriteLine($"[MonogameRenderer] {sig}");
            }
        }

        /// <summary>Push the resolved rig into the shared mesh effect (per frame).</summary>
        private void ApplyEnvironmentAndLights()
        {
            _effect.AmbientLightColor = _env.Ambient;

            var d0 = _effect.DirectionalLight0;
            var d1 = _effect.DirectionalLight1;
            var d2 = _effect.DirectionalLight2;
            if (_env.SunCount > 0)
            {
                var dirs = new[] { d0, d1, d2 };
                for (int i = 0; i < 3; i++)
                {
                    bool on = i < _env.SunCount;
                    dirs[i].Enabled = on;
                    if (on)
                    {
                        dirs[i].Direction = _env.SunDirs[i];
                        dirs[i].DiffuseColor = _env.SunColors[i];
                        dirs[i].SpecularColor = Vector3.Zero;
                    }
                }
            }
            else
            {
                // Baked-in default rig: warm key sun + cool sky fill + ground bounce.
                d0.Enabled = true;
                d0.Direction = DefaultSunDir;
                d0.DiffuseColor = DefaultSunColor;
                d0.SpecularColor = Vector3.Zero;
                d1.Enabled = true;
                d1.Direction = DefaultFillDir;
                d1.DiffuseColor = DefaultFillColor;
                d1.SpecularColor = Vector3.Zero;
                d2.Enabled = true;
                d2.Direction = Vector3.Up;
                d2.DiffuseColor = DefaultBounceColor;
                d2.SpecularColor = Vector3.Zero;
            }
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

        // ── Drawing ──────────────────────────────────────────────────────────

        public void DrawFrame(
            PaperFrame? uiFrame = null,
            IReadOnlyDictionary<long, PaperWorldCanvasSystem.Surface>? worldCanvases = null)
        {
            UpdateFps();

            // Baked-in lighting rig (ambient + sun slots) for the mesh effect.
            ApplyEnvironmentAndLights();

            var packet = _packet;
            var groups = new Dictionary<long, List<MeshDraw>>();
            if (packet != null)
            {
                foreach (var draw in packet.Meshes)
                {
                    if (!groups.TryGetValue(draw.ViewportId, out var list))
                        groups[draw.ViewportId] = list = new List<MeshDraw>();
                    list.Add(draw);
                }
            }

            // Pass 1: viewport groups — render each into its own RenderTarget2D sized
            // to the viewport panel Paper laid out. The UI packet carries the rect and
            // receives the finished texture back for the UI draw pass.
            foreach (var group in groups)
            {
                if (group.Key == 0) continue;
                var viewport = FindViewport(uiFrame, group.Key);
                if (viewport == null) continue;

                int vpWidth = (int)MathF.Round(viewport.Rect.Z);
                int vpHeight = (int)MathF.Round(viewport.Rect.W);
                if (vpWidth <= 0 || vpHeight <= 0) continue;

                var rt = GetOrCreateViewportTarget(group.Key, vpWidth, vpHeight);
                viewport.Texture = rt;
                _gd.SetRenderTarget(rt);
                _gd.Viewport = new Viewport(0, 0, rt.Width, rt.Height);
                _gd.Clear(Unpack(viewport.Color));
                _gd.DepthStencilState = DepthStencilState.Default;
                // Explicit: the previous UI/2D pass can leave a culling state behind
                // that flips V12's faces (inside-out look).
                _gd.RasterizerState = RasterizerState.CullNone;
                _gd.BlendState = BlendState.Opaque;

                float aspect = rt.Height > 0 ? (float)rt.Width / rt.Height : 16f / 9f;
                ResolveCamera(aspect, out var vpView, out var vpProj);
                if (_env.Skybox) DrawSky(vpView, vpProj);
                DrawMeshes(group.Value, vpView, vpProj);
                DrawWorldQuads(vpView, vpProj, worldCanvases);

                _gd.SetRenderTarget(null);
                _gd.Viewport = new Viewport(0, 0, _gd.PresentationParameters.BackBufferWidth, _gd.PresentationParameters.BackBufferHeight);
            }

            // Pass 2: main screen meshes (ViewportId == 0).
            _gd.SetRenderTarget(null);
            _gd.Viewport = new Viewport(0, 0, _gd.PresentationParameters.BackBufferWidth, _gd.PresentationParameters.BackBufferHeight);
            // Dark editor backdrop (UI panels are transparent; cornflower would shine through).
            _gd.Clear(_env.ClearColor);
            _gd.DepthStencilState = DepthStencilState.Default;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.BlendState = BlendState.Opaque;

            float screenAspect = _gd.Viewport.Height > 0 ? (float)_gd.Viewport.Width / _gd.Viewport.Height : 16f / 9f;
            ResolveCamera(screenAspect, out var view, out var projection);
            if (_env.Skybox) DrawSky(view, projection);
            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Console.WriteLine($"[MonogameRenderer] first frame: {packet?.Meshes.Count ?? 0} mesh(es), camera '{_lastCameraName}'");
            }
            if (groups.TryGetValue(0, out var mainMeshes))
                DrawMeshes(mainMeshes, view, projection);
            DrawWorldQuads(view, projection, worldCanvases);
            // Overlay intentionally not invoked on the main pass: the editor
            // gizmo belongs in the viewport RTs where the scene actually shows.
        }

        /// <summary>Ray through a main-screen pixel (backbuffer space).</summary>
        public bool TryGetMainRay(int screenX, int screenY, out Vector3 origin, out Vector3 direction)
        {
            origin = default;
            direction = default;
            var pp = _gd.PresentationParameters;
            if (pp.BackBufferWidth <= 0 || pp.BackBufferHeight <= 0) return false;
            float aspect = pp.BackBufferHeight > 0 ? (float)pp.BackBufferWidth / pp.BackBufferHeight : 16f / 9f;
            ResolveCamera(aspect, out var view, out var proj);

            var screen = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
            var vwp = view * proj;
            var near = screen.Unproject(new Vector3(screenX, screenY, 0f), vwp, Matrix.Identity, Matrix.Identity);
            var far = screen.Unproject(new Vector3(screenX, screenY, 1f), vwp, Matrix.Identity, Matrix.Identity);
            var delta = far - near;
            if (delta.LengthSquared() < 1e-12f) return false;
            origin = near;
            direction = Vector3.Normalize(delta);
            return true;
        }

        /// <summary>Topmost world canvas under a screen pixel, with canvas-local pixel coordinates.</summary>
        public bool TryGetWorldCanvasAt(
            int screenX, int screenY,
            IReadOnlyDictionary<long, PaperWorldCanvasSystem.Surface>? canvases,
            out long canvasId, out float pixelX, out float pixelY)
        {
            canvasId = 0;
            pixelX = 0f;
            pixelY = 0f;
            if (canvases == null || canvases.Count == 0) return false;
            if (!TryGetMainRay(screenX, screenY, out var origin, out var dir)) return false;

            float bestT = float.MaxValue;
            foreach (var surface in canvases.Values)
            {
                if (!_mirrorWorlds.TryGetValue(surface.Id, out var world)) continue;
                var quad = GetWorldCanvasQuad(world, surface.PixelWidth, surface.PixelHeight);

                var normal = Vector3.Cross(quad.AxisX, quad.AxisY);
                if (normal.LengthSquared() < 1e-12f) continue;
                normal.Normalize();

                float denom = Vector3.Dot(dir, normal);
                if (Math.Abs(denom) < 1e-9f) continue;
                float t = Vector3.Dot(normal, quad.Center - origin) / denom;
                if (t <= 0f || t >= bestT) continue;

                var hit = origin + dir * t;
                float lx = Vector3.Dot(hit - quad.Center, quad.AxisX);
                float ly = Vector3.Dot(hit - quad.Center, quad.AxisY);
                if (Math.Abs(lx) > quad.Width / 2f || Math.Abs(ly) > quad.Height / 2f) continue;

                bestT = t;
                canvasId = surface.Id;
                pixelX = (lx / quad.Width + 0.5f) * surface.PixelWidth;
                pixelY = (0.5f - ly / quad.Height) * surface.PixelHeight;
            }

            return canvasId != 0;
        }

        private void DrawWorldQuads(
            Matrix view, Matrix projection,
            IReadOnlyDictionary<long, PaperWorldCanvasSystem.Surface>? canvases)
        {
            if (canvases == null || canvases.Count == 0) return;

            EvictQuadCache(canvases);

            _quadEffect.View = view;
            _quadEffect.Projection = projection;
            _gd.BlendState = BlendState.AlphaBlend;
            _gd.DepthStencilState = DepthStencilState.Default;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.SamplerStates[0] = SamplerState.LinearClamp;

            foreach (var surface in canvases.Values)
            {
                if (surface.Texture is not RenderTarget2D tex || tex.IsDisposed) continue;
                if (!_mirrorWorlds.TryGetValue(surface.Id, out var world)) continue;
                var placement = GetWorldCanvasQuad(world, surface.PixelWidth, surface.PixelHeight);

                if (!_quadCache.TryGetValue(surface.Id, out var quad)
                    || quad.vb.IsDisposed
                    || Math.Abs(quad.w - placement.Width) > 1e-4f
                    || Math.Abs(quad.h - placement.Height) > 1e-4f)
                {
                    if (_quadCache.TryGetValue(surface.Id, out var old) && !old.vb.IsDisposed)
                        old.vb.Dispose();
                    _quadCache[surface.Id] = quad = (BuildQuadBuffer(placement.Width, placement.Height), placement.Width, placement.Height);
                }

                _quadEffect.World = placement.World;
                _quadEffect.Texture = tex;
                _gd.SetVertexBuffer(quad.vb);

                foreach (var pass in _quadEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
                }
            }
            _gd.SetVertexBuffer(null);
        }

        private static (Matrix World, Vector3 Center, Vector3 AxisX, Vector3 AxisY, float Width, float Height)
            GetWorldCanvasQuad(System.Numerics.Matrix4x4 world, int pixelWidth, int pixelHeight)
        {
            var matrix = V12MonogameMath.ToXna(world);
            var center = new Vector3(matrix.M41, matrix.M42, matrix.M43);
            var axisX = new Vector3(matrix.M11, matrix.M21, matrix.M31);
            var axisY = new Vector3(matrix.M12, matrix.M22, matrix.M32);
            float scaleX = axisX.Length();
            float scaleY = axisY.Length();

            bool hasScale = (scaleX > 0.01f && Math.Abs(scaleX - 1f) > 1e-3f) ||
                            (scaleY > 0.01f && Math.Abs(scaleY - 1f) > 1e-3f);

            if (scaleX > 0.01f)
            {
                matrix.M11 /= scaleX; matrix.M21 /= scaleX; matrix.M31 /= scaleX;
                axisX = new Vector3(matrix.M11, matrix.M21, matrix.M31);
            }
            else
            {
                axisX = Vector3.UnitX;
            }

            if (scaleY > 0.01f)
            {
                matrix.M12 /= scaleY; matrix.M22 /= scaleY; matrix.M32 /= scaleY;
                axisY = new Vector3(matrix.M12, matrix.M22, matrix.M32);
            }
            else
            {
                axisY = Vector3.UnitY;
            }

            float width = hasScale ? Math.Max(0.01f, scaleX) : WorldCanvasWidthMetres;
            float height = hasScale
                ? Math.Max(0.01f, scaleY)
                : WorldCanvasWidthMetres * pixelHeight / Math.Max(1, pixelWidth);

            return (matrix, center, axisX, axisY, width, height);
        }

        private VertexBuffer BuildQuadBuffer(float w, float h)
        {
            float hw = w / 2f, hh = h / 2f;
            var verts = new[]
            {
                new VertexPositionTexture(new Vector3(-hw, hh, 0f), new Vector2(0f, 0f)),
                new VertexPositionTexture(new Vector3(hw, hh, 0f), new Vector2(1f, 0f)),
                new VertexPositionTexture(new Vector3(-hw, -hh, 0f), new Vector2(0f, 1f)),
                new VertexPositionTexture(new Vector3(hw, -hh, 0f), new Vector2(1f, 1f)),
            };
            var vb = new VertexBuffer(_gd, VertexPositionTexture.VertexDeclaration, 4, BufferUsage.WriteOnly);
            vb.SetData(verts);
            return vb;
        }

        private void EvictQuadCache(IReadOnlyDictionary<long, PaperWorldCanvasSystem.Surface> canvases)
        {
            List<long>? dead = null;
            foreach (var id in _quadCache.Keys)
                if (!canvases.ContainsKey(id)) (dead ??= new List<long>()).Add(id);
            if (dead == null) return;
            foreach (var id in dead)
            {
                if (_quadCache.TryGetValue(id, out var quad) && !quad.vb.IsDisposed)
                    quad.vb.Dispose();
                _quadCache.Remove(id);
            }
        }

        private static PaperViewportInfo? FindViewport(PaperFrame? frame, long id)
        {
            if (frame == null) return null;
            for (int i = 0; i < frame.Viewports.Count; i++)
                if (frame.Viewports[i].Id == id) return frame.Viewports[i];
            return null;
        }

        private static Color Unpack(uint rgba) => new(
            (byte)(rgba & 0xFF),
            (byte)((rgba >> 8) & 0xFF),
            (byte)((rgba >> 16) & 0xFF),
            (byte)((rgba >> 24) & 0xFF));

        private void DrawMeshes(IEnumerable<MeshDraw> meshes, Matrix view, Matrix projection)
        {
            _effect.View = view;
            _effect.Projection = projection;
            _effect.World = Matrix.Identity;
            // Explicit every call: earlier passes (sky) leave depth off / blend
            // changed, and without this meshes draw in submission order with
            // coplanar faces flickering dark instead of depth-resolved.
            _gd.DepthStencilState = DepthStencilState.Default;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.BlendState = BlendState.Opaque;
            // NOTE: no EnableDefaultLighting() here — the baked-in rig from
            // ApplyEnvironmentAndLights owns the directional slots per frame.
            _effect.LightingEnabled = true;
            _effect.PreferPerPixelLighting = true;
            _effect.VertexColorEnabled = true;
            _effect.TextureEnabled = false;
            _effect.Texture = null;
            _effect.DiffuseColor = Vector3.One;
            // Crisp texel edges for blocky albedo maps (Minecraft-style
            // pixels); linear would smear them at oblique angles.
            _gd.SamplerStates[0] = SamplerState.PointClamp;

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

                // Per-mesh albedo texture from the owner's MaterialComponent
                // (same lookup ResolveColor uses). Untextured or unresolvable
                // paths fall back to the flat vertex colour.
                var texture = ResolveTexture(ResolveMaterial(mesh)?.PrimaryTexture);
                _effect.TextureEnabled = texture != null;
                _effect.Texture = texture;

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

        /// <summary>
        /// Draw the gradient sky dome centered on the viewer. Unlit, depthless,
        /// first in the pass: opaque scene geometry overwrites it. Rebuilt only
        /// when the gradient colors change.
        /// </summary>
        private void DrawSky(Matrix view, Matrix projection)
        {
            if (!SkyMatches(_env.Sky, _env.Horizon, _env.Ground))
                RebuildSkyDome(_env.Sky, _env.Horizon, _env.Ground);
            if (_skyVb == null || _skyVb.IsDisposed || _skyIb == null || _skyIb.IsDisposed)
                return;

            // Viewer position from the view matrix (rigid lookAt): eye = -R^T * t.
            var eye = new Vector3(
                -(view.M41 * view.M11 + view.M42 * view.M21 + view.M43 * view.M31),
                -(view.M41 * view.M12 + view.M42 * view.M22 + view.M43 * view.M32),
                -(view.M41 * view.M13 + view.M42 * view.M23 + view.M43 * view.M33));

            _skyEffect.World = Matrix.CreateTranslation(eye);
            _skyEffect.View = view;
            _skyEffect.Projection = projection;
            _gd.BlendState = BlendState.Opaque;
            _gd.DepthStencilState = DepthStencilState.None;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.SetVertexBuffer(_skyVb);
            _gd.Indices = _skyIb;

            foreach (var pass in _skyEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _skyPrimCount);
            }
            _gd.SetVertexBuffer(null);
        }

        private bool SkyMatches(Vector3 top, Vector3 horizon, Vector3 ground) =>
            _skyBuilt
            && (_skyTop - top).LengthSquared() < 1e-8f
            && (_skyHorizon - horizon).LengthSquared() < 1e-8f
            && (_skyBottom - ground).LengthSquared() < 1e-8f;

        private void RebuildSkyDome(Vector3 top, Vector3 horizon, Vector3 bottom)
        {
            const int lon = 24;
            const int lat = 12;

            var verts = new List<VertexPositionNormalColor>((lat + 1) * (lon + 1));
            for (int iy = 0; iy <= lat; iy++)
            {
                float v = (float)iy / lat; // 0 = top pole, 1 = bottom pole
                float y = MathF.Cos(v * MathF.PI);
                float ring = MathF.Sin(v * MathF.PI);
                Color color;
                if (y >= 0f)
                    color = ToColor(Vector3.Lerp(horizon, top, MathF.Pow(y, 0.65f)));
                else
                    color = ToColor(Vector3.Lerp(horizon, bottom, MathF.Pow(-y, 0.5f)));
                for (int ix = 0; ix <= lon; ix++)
                {
                    float u = (float)ix / lon;
                    float a = u * MathF.PI * 2f;
                    verts.Add(new VertexPositionNormalColor(
                        new Vector3(MathF.Cos(a) * ring * SkyDomeRadius, y * SkyDomeRadius, MathF.Sin(a) * ring * SkyDomeRadius),
                        Vector3.Up,
                        color));
                }
            }

            var indices = new List<int>(lat * lon * 6);
            int stride = lon + 1;
            for (int iy = 0; iy < lat; iy++)
            {
                for (int ix = 0; ix < lon; ix++)
                {
                    int a = iy * stride + ix;
                    int b = a + 1;
                    int c = a + stride;
                    int d = c + 1;
                    indices.Add(a); indices.Add(c); indices.Add(b);
                    indices.Add(b); indices.Add(c); indices.Add(d);
                }
            }

            if (_skyVb != null && !_skyVb.IsDisposed) _skyVb.Dispose();
            if (_skyIb != null && !_skyIb.IsDisposed) _skyIb.Dispose();
            _skyVb = new VertexBuffer(_gd, VertexPositionNormalColor.VertexDeclaration, verts.Count, BufferUsage.WriteOnly);
            _skyVb.SetData(verts.ToArray());
            _skyIb = new IndexBuffer(_gd, IndexElementSize.ThirtyTwoBits, indices.Count, BufferUsage.WriteOnly);
            _skyIb.SetData(indices.ToArray());
            _skyPrimCount = indices.Count / 3;

            _skyTop = top;
            _skyHorizon = horizon;
            _skyBottom = bottom;
            _skyBuilt = true;
        }

        private static Color ToColor(Vector3 v) => new(
            Math.Clamp(v.X, 0f, 1f), Math.Clamp(v.Y, 0f, 1f), Math.Clamp(v.Z, 0f, 1f), 1f);

        private static bool Finite(Vector3 v) =>
            !(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)
              || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z));

        private static Vector3 Sanitize(Vector3 v, Vector3 fallback, ref bool clean)
        {
            if (Finite(v)) return v;
            clean = false;
            return fallback;
        }

        private static Vector3 SanitizeDir(Vector3 v, Vector3 fallback, ref bool clean)
        {
            if (Finite(v) && v.LengthSquared() > 1e-6f) return Vector3.Normalize(v);
            clean = false;
            return fallback;
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
                    BuildViewProjection(V12MonogameMath.ToXna(rs.Transform), rs.Fov, rs.NearClip, rs.FarClip, rs.Projection, rs.OrthoSize, aspect, out view, out projection);
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
                camera.FieldOfView, camera.NearClip, camera.FarClip,
                camera.Projection, camera.OrthoSize, aspect,
                out view, out projection);
        }

        private static void BuildViewProjection(Matrix world, float fovDegrees, float near, float far, CameraProjection projectionMode, float orthoSize, float aspect, out Matrix view, out Matrix projection)
        {
            var position = new Vector3(world.M41, world.M42, world.M43);
            var forward = new Vector3(-world.M31, -world.M32, -world.M33);
            var up = new Vector3(world.M21, world.M22, world.M23);

            if (forward.LengthSquared() < 1e-6f) forward = -Vector3.UnitZ;
            if (up.LengthSquared() < 1e-6f) up = Vector3.Up;
            forward.Normalize();
            up.Normalize();

            view = Matrix.CreateLookAt(position, position + forward, up);
            if (projectionMode == CameraProjection.Orthographic)
            {
                float halfSize = orthoSize > 0f ? orthoSize : 5f;
                float halfHeight = halfSize;
                float halfWidth = halfHeight * aspect;
                projection = Matrix.CreateOrthographic(
                    halfWidth * 2f,
                    halfHeight * 2f,
                    near <= 0f ? 0.05f : near,
                    far <= near ? near + 1000f : far);
            }
            else
            {
                projection = Matrix.CreatePerspectiveFieldOfView(
                    MathHelper.ToRadians(fovDegrees <= 0f ? 75f : fovDegrees),
                    aspect,
                    near <= 0f ? 0.05f : near,
                    far <= near ? near + 1000f : far);
            }
        }

        private GpuMesh? GetOrBuildGpuMesh(IMeshRenderable mesh)
        {
            var points = mesh.MeshPoints;
            var indices = mesh.Indices;
            if (points == null || indices == null || points.Length < 9 || indices.Length < 3)
                return null;

            var color = ResolveColor(mesh);
            var material = ResolveMaterial(mesh);
            float uvScaleX = material?.Uv1ScaleX ?? 1f;
            float uvScaleY = material?.Uv1ScaleY ?? 1f;
            float uvOffsetX = material?.Uv1OffsetX ?? 0f;
            float uvOffsetY = material?.Uv1OffsetY ?? 0f;
            // Identity, not length: mesh data arrays are replaced — never
            // mutated in place — so a new array is new geometry even at equal
            // lengths. A length-only key renders stale geometry forever
            // (e.g. gizmo handles resizing while dollying keep their first
            // frame's vertices). Reference hashes are O(1). UV scale/offset
            // fold in too: they change the baked UVs, not just a uniform.
            long signature = ((long)(uint)RuntimeHelpers.GetHashCode(points) << 32)
                ^ (uint)RuntimeHelpers.GetHashCode(indices)
                ^ ((long)color.PackedValue << 1)
                ^ ((long)BitConverter.DoubleToInt64Bits(uvScaleX) << 3)
                ^ (long)BitConverter.DoubleToInt64Bits(uvScaleY)
                ^ ((long)BitConverter.DoubleToInt64Bits(uvOffsetX) >> 5)
                ^ ((long)BitConverter.DoubleToInt64Bits(uvOffsetY) >> 11);

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

            // Planar UVs per vertex, picked by its dominant normal axis and
            // normalised over the mesh bounding box: every face of an
            // axis-aligned box maps onto the full 0..1 texture — exactly what
            // a Minecraft-style cube_all block wants. The material's UV
            // scale/offset then tile/shift the result. Meshes without a
            // texture never read these (TextureEnabled stays off for them).
            BakePlanarUvs(vertices, uvScaleX, uvScaleY, uvOffsetX, uvOffsetY);

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
            var material = ResolveMaterial(mesh);
            if (material != null)
                return new Color(material.R, material.G, material.B, material.A);
            return new Color(0.8f, 0.8f, 0.8f, 1f);
        }

        /// <summary>The owner element's MaterialComponent, or null (same
        /// lookup path every material-driven decision uses).</summary>
        private static MaterialComponent? ResolveMaterial(IMeshRenderable mesh)
        {
            if (mesh is ComponentBase component && component.Owner != null)
                return component.Owner.GetComponent<MaterialComponent>();
            return null;
        }

        /// <summary>
        /// GPU texture for an albedo path, or null. Paths resolve through the
        /// engine's asset resolver (v12:// URIs and pak-mount-relative paths)
        /// first, then the raw filesystem. Cached by resolved path — a failed
        /// load is negative-cached so it is not retried every frame.
        /// </summary>
        private Texture2D? ResolveTexture(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string? resolved = ResolveTexturePath(path);
            if (resolved == null) return null;

            if (_textures.TryGetValue(resolved, out var cached)) return cached;

            Texture2D? texture = null;
            try
            {
                using var stream = File.OpenRead(resolved);
                texture = Texture2D.FromStream(_gd, stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonogameRenderer] texture load failed '{resolved}': {ex.Message}");
            }
            _textures[resolved] = texture;
            return texture;
        }

        /// <summary>Material texture path → existing absolute file path, or null.</summary>
        private string? ResolveTexturePath(string path)
        {
            var resolver = _root?.Registry?.Get<IAssetResolver>();

            if (path.StartsWith("v12://", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = resolver?.Resolve(path) ?? path;
                return File.Exists(resolved) ? resolved : null;
            }

            if (Path.IsPathRooted(path))
                return File.Exists(path) ? path : null;

            // Relative: check against every mounted pak root, then raw CWD.
            if (resolver is V12AssetResolver mounts)
            {
                var relative = mounts.ResolveRelative(path);
                if (!string.Equals(relative, path, StringComparison.Ordinal) && File.Exists(relative))
                    return relative;
            }
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Bakes planar UVs into <paramref name="vertices"/>: each vertex is
        /// projected along its dominant normal axis onto the mesh bounding
        /// box, normalised to 0..1, then scaled/offset by the material UVs.
        /// +Y (top) faces read the texture with +Z as down, side faces keep
        /// +Y up — the usual Minecraft block convention.
        /// </summary>
        private static void BakePlanarUvs(
            VertexPositionNormalColor[] vertices,
            float scaleX, float scaleY, float offsetX, float offsetY)
        {
            if (vertices.Length == 0) return;

            var min = vertices[0].Position;
            var max = vertices[0].Position;
            for (int i = 1; i < vertices.Length; i++)
            {
                var p = vertices[i].Position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var size = max - min;
            // A degenerate axis (flat plane mesh) projects to 0 instead of NaN.
            float sx = size.X > 1e-6f ? 1f / size.X : 0f;
            float sy = size.Y > 1e-6f ? 1f / size.Y : 0f;
            float sz = size.Z > 1e-6f ? 1f / size.Z : 0f;

            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                var n = v.Normal;
                float ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);

                float u, w; // w = raw v before material transform
                if (ax >= ay && ax >= az)
                {
                    u = (v.Position.Z - min.Z) * sz;
                    w = (max.Y - v.Position.Y) * sy;
                }
                else if (ay >= az)
                {
                    u = (v.Position.X - min.X) * sx;
                    w = (v.Position.Z - min.Z) * sz;
                }
                else
                {
                    u = (v.Position.X - min.X) * sx;
                    w = (max.Y - v.Position.Y) * sy;
                }

                v.TextureCoordinate = new Vector2(u * scaleX + offsetX, w * scaleY + offsetY);
                vertices[i] = v;
            }
        }

        private static void ResolveDrawFlags(IMeshRenderable mesh, out bool unlit, out bool noDepthTest)
        {
            var material = ResolveMaterial(mesh);
            unlit = material?.Unlit ?? false;
            noDepthTest = material?.NoDepthTest ?? false;
        }
    }
}
