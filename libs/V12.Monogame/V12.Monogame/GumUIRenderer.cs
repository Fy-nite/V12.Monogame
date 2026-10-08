using System;
using System.Collections.Generic;
using Gum;
using Gum.Forms;
using Gum.Forms.Controls;
using Gum.Wireframe;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Gum.GueDeriving;
using V12.Core;
using V12.Core.Interfaces;
using V12.Core.Rendering;

namespace V12.Monogame
{
    /// <summary>
    /// A Gum-backed <see cref="IUIRenderer"/>. V12 hands it a <see cref="UIFrame"/> (screen-space
    /// canvases + widgets); this reconciles Gum Forms controls keyed by element id — it never
    /// walks the V12 world tree itself.
    ///
    /// Supported widget kinds: Canvas, Panel, Label, Button, Toggle/Checkbox, Slider, TextInput,
    /// Rect (colored rectangle), Image, Icon (glyph or texture), ProgressBar (track+fill),
    /// Viewport (3D scene sprite — see <see cref="CreateViewport"/>) and Scroll (clipped
    /// scroll container: wheel-driven offset, children nested for clipping).
    /// </summary>
    public sealed class GumUIRenderer : IUIRenderer
    {
        private const float DefaultBaseFontSize = 16f;

        private static readonly Color DefaultTrackColor = new(0x2a, 0x2a, 0x2e);
        private static readonly Color DefaultRectColor = new(0x2b, 0x2b, 0x31);
        private static readonly Color DefaultViewportColor = new(0x1a, 0x1a, 0x20);
        private static readonly Color AccentColor = new(0x4c, 0xc2, 0xff);

        private readonly Game _game;
        private readonly GameRoot _root;
        private readonly Dictionary<long, FrameworkElement> _controls = new();
        private readonly Dictionary<long, string> _imageSources = new();
        private readonly Dictionary<long, SpriteRuntime> _viewportSprites = new();
        private readonly Dictionary<long, Rectangle> _viewportRects = new();
        private readonly Dictionary<long, Color> _viewportColors = new();
        private bool _initialized;
        private bool _loggedFirstFrame;
        private bool _loggedWorldSpaceCanvas;
        private bool _loggedViewportPlaceholder;
        private bool _loggedLayoutDump;
        private readonly Dictionary<long, Rectangle> _lastRects = new();
        // Scroll state: per-panel wheel offset, last measured content height,
        // live scroll-panel ids (for wheel hit-testing), nesting flags and the
        // current visual parent (0 = Gum root) for clip-correct reparenting.
        private readonly Dictionary<long, float> _scrollOffsets = new();
        private readonly Dictionary<long, float> _scrollContentH = new();
        private readonly HashSet<long> _scrollIds = new();
        private readonly Dictionary<long, bool> _nested = new();
        private readonly Dictionary<long, long> _visualParent = new();
        private int _lastWheel;

        public GumUIRenderer(Game game, GameRoot root)
        {
            _game = game;
            _root = root;
            Initialize(game);
        }

        private void Initialize(Game game)
        {
            GumService.Default.Initialize(game, DefaultVisualsVersion.V3);
            GumService.Default.ContentLoader.XnaContentManager = game.Content;
            FrameworkElement.KeyboardsForUiControl.Add(GumService.Default.Keyboard);

            // Tab is used by the host to toggle mouse capture, so drop Gum's default
            // Tab/Shift+Tab focus navigation (UI is mouse-driven in this pass).
            FrameworkElement.TabKeyCombos.Clear();
            FrameworkElement.TabReverseKeyCombos.Clear();

            var pp = game.GraphicsDevice.PresentationParameters;
            GumService.Default.CanvasWidth = pp.BackBufferWidth;
            GumService.Default.CanvasHeight = pp.BackBufferHeight;

            _initialized = true;
            Console.WriteLine("[GumUIRenderer] Gum initialized");
        }

        // ── IUIRenderer ──────────────────────────────────────────────────────

        public void ApplyUI(UIFrame frame) => Reconcile(frame?.Nodes);

        public void step() { /* Gum is driven by the host loop (Update/Draw) */ }

        /// <summary>Drive Gum's per-frame update (call from the host's Update).</summary>
        public void Update(GameTime gameTime)
        {
            // Keep the Gum canvas matched to the backbuffer (window resize/maximize).
            var pp = _game.GraphicsDevice.PresentationParameters;
            if (pp.BackBufferWidth > 0 && pp.BackBufferHeight > 0 &&
                (GumService.Default.CanvasWidth != pp.BackBufferWidth || GumService.Default.CanvasHeight != pp.BackBufferHeight))
            {
                GumService.Default.CanvasWidth = pp.BackBufferWidth;
                GumService.Default.CanvasHeight = pp.BackBufferHeight;
            }
            UpdateScrollWheel();
            GumService.Default.Update(gameTime);
        }

        /// <summary>
        /// Mouse-wheel scrolling for Scroll panels: the topmost (smallest) panel
        /// under the cursor absorbs the wheel delta; the next dirty-frame layout
        /// clamps and applies the offset. Coordinates are backbuffer pixels,
        /// which the Gum canvas tracks 1:1 (see above).
        /// </summary>
        private void UpdateScrollWheel()
        {
            if (!_game.IsActive || _scrollIds.Count == 0) return;
            var mouse = Microsoft.Xna.Framework.Input.Mouse.GetState();
            int delta = mouse.ScrollWheelValue - _lastWheel;
            _lastWheel = mouse.ScrollWheelValue;
            if (delta == 0) return;

            long best = 0;
            int bestArea = int.MaxValue;
            foreach (var id in _scrollIds)
            {
                if (!_lastRects.TryGetValue(id, out var r)) continue;
                if (!r.Contains(mouse.Position)) continue;
                int area = r.Width * r.Height;
                if (area < bestArea) { bestArea = area; best = id; }
            }
            if (best == 0) return;

            _scrollOffsets.TryGetValue(best, out var off);
            off -= (delta / 120f) * 48f; // wheel up = content moves down = offset shrinks
            if (off < 0f) off = 0f; // upper clamp happens in Arrange (knows content height)
            _scrollOffsets[best] = off;
            _root.MarkRenderDirty();
        }

        /// <summary>Draw the Gum UI (call from the host's Draw, after the 3D pass).</summary>
        public void Draw()
        {
            // Hand each viewport's render-target to its sprite before Gum composites.
            var renderer = _root.Registry.Get<MonogameV12Renderer>();
            if (renderer != null)
            {
                foreach (var kvp in _viewportSprites)
                    kvp.Value.Texture = renderer.GetViewportTexture(kvp.Key);
            }
            GumService.Default.Draw();
        }

        /// <summary>Rect (screen space) and clear color of a viewport panel, or false when unknown.</summary>
        public bool TryGetViewportRect(long id, out Rectangle rect, out Color color)
        {
            if (_viewportRects.TryGetValue(id, out rect) && _viewportColors.TryGetValue(id, out color)) return true;
            rect = default; color = default; return false;
        }

        /// <summary>Viewport under a screen-space point (smallest wins when
        /// nested), for editor orbit input. False when over no viewport.</summary>
        public bool TryGetViewportAt(Point position, out long id, out Rectangle rect)
        {
            id = 0;
            rect = default;
            int bestArea = int.MaxValue;
            foreach (var kvp in _viewportRects)
            {
                if (!kvp.Value.Contains(position)) continue;
                int area = kvp.Value.Width * kvp.Value.Height;
                if (area < bestArea) { bestArea = area; id = kvp.Key; rect = kvp.Value; }
            }
            return id != 0;
        }

        // ── Reconciliation ───────────────────────────────────────────────────

        private void Reconcile(List<UINode>? nodes)
        {
            if (!_initialized) return;

            // Sync the Gum canvas to the backbuffer BEFORE Layout runs below.
            // V12Game marks a dirty frame on resize, but GumUIRenderer.Update
            // (which also syncs) runs after V12Tick captures UI — so without
            // this, layout uses the stale canvas size and the window freezes
            // in its old arrangement until the next world edit dirties again.
            var pp = _game.GraphicsDevice.PresentationParameters;
            if (pp.BackBufferWidth > 0 && pp.BackBufferHeight > 0 &&
                (GumService.Default.CanvasWidth != pp.BackBufferWidth || GumService.Default.CanvasHeight != pp.BackBufferHeight))
            {
                GumService.Default.CanvasWidth = pp.BackBufferWidth;
                GumService.Default.CanvasHeight = pp.BackBufferHeight;
            }

            if (nodes == null || nodes.Count == 0)
            {
                if (_controls.Count > 0)
                {
                    GumService.Default.Root.Children.Clear();
                    _controls.Clear();
                }
                return;
            }

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Console.WriteLine($"[GumUIRenderer] first UI frame: {nodes.Count} node(s)");
            }

            var seen = new HashSet<long>();
            var byId = new Dictionary<long, UINode>();
            foreach (var n in nodes) { seen.Add(n.Id); byId[n.Id] = n; }

            // Remove controls whose element is gone this frame.
            List<long>? stale = null;
            foreach (var id in _controls.Keys)
                if (!seen.Contains(id)) (stale ??= new List<long>()).Add(id);

            if (stale != null)
            {
                foreach (var id in stale)
                {
                    var control = _controls[id];
                    control.Visual.RemoveFromRoot();
                    if (control.Visual.Parent != null)
                        control.Visual.Parent.Children.Remove(control.Visual);
                    _controls.Remove(id);
                    _viewportSprites.Remove(id);
                    _scrollOffsets.Remove(id);
                    _scrollContentH.Remove(id);
                    _visualParent.Remove(id);
                }
            }

            // Layout parent map (nearest container ancestor) — shared by visual
            // attach (scroll subtrees nest for clipping) and Layout().
            var children = BuildChildrenMap(nodes, byId);
            var visMemo = new Dictionary<long, long>();

            // Create / update (parents precede children in capture order).
            foreach (var node in nodes)
            {
                if (node.Kind == UIWidgetKind.Canvas && !node.ScreenSpace && !_loggedWorldSpaceCanvas)
                {
                    _loggedWorldSpaceCanvas = true;
                    Console.WriteLine("[GumUIRenderer] world-space canvas: drawn as a screen overlay (bake-to-quad deferred)");
                }

                if (!_controls.TryGetValue(node.Id, out var control))
                {
                    control = Create(node);
                    _controls[node.Id] = control;

                    // Flat attach: every control is positioned in absolute canvas
                    // pixels by Layout/SetBounds, so nesting visuals would only add
                    // ancestor offsets (and odd inner containers like LabelVisual).
                    // Capture order is parent-first, which keeps correct paint order.
                    control.AddToRoot();
                }

                Update(node, control);
                AttachVisual(node, byId, visMemo);
            }

            Layout(nodes, byId, children);
        }

        /// <summary>
        /// Desired visual parent for a node: the node itself stays at the Gum
        /// root unless it lives inside a Scroll subtree, in which case it nests
        /// under its effective layout parent so the panel's ClipsChildren
        /// scissor actually covers it (flat-attached siblings are NOT clipped).
        /// Returns 0 for Gum-root attach.
        /// </summary>
        private long DesiredVisualParent(UINode node, Dictionary<long, UINode> byId, Dictionary<long, long> memo)
        {
            if (node.Kind == UIWidgetKind.Canvas) return 0;
            if (memo.TryGetValue(node.Id, out var m)) return m;
            memo[node.Id] = 0; // provisional: breaks ParentId cycles (corrupt frames fall back to root)
            if (node.ParentId == 0 || !byId.TryGetValue(node.ParentId, out var p)) return 0;
            var eff = p;
            while (!IsContainer(eff.Kind))
            {
                if (eff.ParentId == 0 || !byId.TryGetValue(eff.ParentId, out var up)) break;
                eff = up;
            }
            long result;
            if (eff.Kind == UIWidgetKind.Scroll) result = eff.Id;
            else if (eff.Kind == UIWidgetKind.Canvas || eff.Id == node.Id) result = 0;
            else result = DesiredVisualParent(eff, byId, memo) != 0 ? eff.Id : 0;
            memo[node.Id] = result;
            return result;
        }

        private void AttachVisual(UINode node, Dictionary<long, UINode> byId, Dictionary<long, long> memo)
        {
            long want = DesiredVisualParent(node, byId, memo);
            _visualParent.TryGetValue(node.Id, out var cur);
            if (cur == want) return;
            if (!_controls.TryGetValue(node.Id, out var control)) return;
            // Detach from wherever it currently lives.
            if (cur != 0 && _controls.TryGetValue(cur, out var oldParent))
                oldParent.Visual.Children.Remove(control.Visual);
            else
                control.Visual.RemoveFromRoot();
            // Attach at the new home (fall back to root when it vanished).
            if (want != 0 && _controls.TryGetValue(want, out var newParent))
                newParent.AddChild(control);
            else
            {
                control.AddToRoot();
                want = 0;
            }
            _visualParent[node.Id] = want;
        }

        // ── Control creation ─────────────────────────────────────────────────

        private FrameworkElement Create(UINode node)
        {
            switch (node.Kind)
            {
                case UIWidgetKind.Canvas:
                {
                    var panel = new Panel();
                    panel.Dock(Gum.Wireframe.Dock.Fill);
                    // Layout roots are invisible positioning shells — and with flat
                    // attach their visuals sit ABOVE nothing but BELOW later siblings
                    // except for stray childless canvases (e.g. the UIBuilder root),
                    // which land on top of everything. Either way they must never
                    // swallow mouse input meant for the widgets: opt out of
                    // hit-testing (no visual children ever nest under a canvas).
                    panel.Visual.HasEvents = false;
                    return panel;
                }
                case UIWidgetKind.Button:
                {
                    var button = new Button();
                    var onClick = node.OnClick;
                    if (onClick != null) button.Click += (_, __) => onClick();
                    return button;
                }
                case UIWidgetKind.Label:
                    return new Label();

                case UIWidgetKind.Toggle:
                case UIWidgetKind.Checkbox:
                {
                    var check = new CheckBox();
                    return check;
                }
                case UIWidgetKind.Slider:
                {
                    var slider = new Slider();
                    var onChanged = node.OnValueChanged;
                    if (onChanged != null) slider.ValueChanged += (_, __) => onChanged((float)slider.Value);
                    return slider;
                }
                case UIWidgetKind.TextInput:
                {
                    var textBox = new TextBox();
                    var onChanged = node.OnTextChanged;
                    if (onChanged != null) textBox.TextChanged += (_, __) => onChanged(textBox.Text ?? "");
                    return textBox;
                }
                case UIWidgetKind.Rect:
                case UIWidgetKind.Panel:
                    return CreateRect(node);

                case UIWidgetKind.Image:
                    return CreateImage(node);

                case UIWidgetKind.Icon:
                    // Icon ids that look like file paths render as textures; everything
                    // else renders as a text glyph. (A node switching between the two
                    // across frames keeps its original control — rare and harmless.)
                    return LooksLikeTexturePath(node.ImageSource) ? CreateImage(node) : CreateIconGlyph(node);

                case UIWidgetKind.ProgressBar:
                    return CreateProgressBar(node);

                case UIWidgetKind.Viewport:
                    return CreateViewport(node);

                case UIWidgetKind.Splitter:
                case UIWidgetKind.Tree:
                case UIWidgetKind.Scroll:
                {
                    var panel = new Panel();
                    panel.Visual.ClipsChildren = true;
                    return panel;
                }

                default:
                    // HLayout / VLayout / anything unknown: plain container (children are
                    // positioned by the manual flow layout below).
                    return new Panel();
            }
        }

        /// <summary>A colored rectangle (Rect widget or Panel background). Sized to fill its shell.</summary>
        private static FrameworkElement CreateRect(UINode node)
        {
            var shell = new Panel();

            var rect = new Gum.GueDeriving.RectangleRuntime();
            rect.IsFilled = true;
            rect.FillColor = ParseColor(node.Color) ?? DefaultRectColor;
            rect.StrokeWidth = 0;
            if (node.CornerRadius > 0f) rect.CornerRadius = node.CornerRadius;
            rect.X = 0;
            rect.Y = 0;
            rect.Width = 100f;
            rect.WidthUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;
            rect.Height = 100f;
            rect.HeightUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;

            shell.Visual.AddChild(rect);
            shell.Visual.Width = node.Width > 0f ? node.Width : 100f;
            shell.Visual.Height = node.Height > 0f ? node.Height : 100f;
            return shell;
        }

        private static FrameworkElement CreateImage(UINode node)
        {
            var image = new Image();
            if (node.Width <= 0f) { image.Visual.Width = 64f; image.Visual.Height = 64f; }
            return image;
        }

        private static FrameworkElement CreateIconGlyph(UINode node)
        {
            var glyph = new Label();
            if (node.Width <= 0f && node.Height <= 0f)
            {
                glyph.Visual.Width = 24f;
                glyph.Visual.Height = 24f;
            }
            return glyph;
        }

        /// <summary>A ProgressBar composed of a track rectangle and a child fill rectangle.</summary>
        private static FrameworkElement CreateProgressBar(UINode node)
        {
            var shell = new Panel();

            var track = new Gum.GueDeriving.ColoredRectangleRuntime();
            track.Color = DefaultTrackColor;
            track.X = 0;
            track.Y = 0;
            track.Width = 100f;
            track.WidthUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;
            track.Height = 100f;
            track.HeightUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;
            track.ClipsChildren = true;

            var fill = new Gum.GueDeriving.ColoredRectangleRuntime();
            fill.Color = AccentColor;
            fill.X = 0;
            fill.Y = 0;
            fill.Width = 0f; // set per-frame from Value
            fill.WidthUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;
            fill.Height = 100f;
            fill.HeightUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;

            track.AddChild(fill);
            shell.Visual.AddChild(track);

            shell.Visual.Width = node.Width > 0f ? node.Width : 140f;
            shell.Visual.Height = node.Height > 0f ? node.Height : 18f;
            return shell;
        }

        /// <summary>
        /// Placeholder for a 3D scene viewport: a rounded panel tinted with the viewport's
        /// background color and a centered hint label. Real render-target-to-sprite support
        /// needs the 3D renderer to draw into a <c>RenderTarget2D</c> per viewport first.
        /// </summary>
        private FrameworkElement CreateViewport(UINode node)
        {
            if (!_loggedViewportPlaceholder)
            {
                _loggedViewportPlaceholder = true;
                Console.WriteLine("[GumUIRenderer] viewport: render-target-to-sprite active");
            }

            var shell = new Panel();

            var backdrop = new Gum.GueDeriving.RectangleRuntime();
            backdrop.IsFilled = true;
            backdrop.FillColor = ParseColor(node.Color) ?? DefaultViewportColor;
            backdrop.StrokeWidth = 0;
            backdrop.CornerRadius = 6f;
            backdrop.X = 0;
            backdrop.Y = 0;
            backdrop.Width = 100f;
            backdrop.WidthUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;
            backdrop.Height = 100f;
            backdrop.HeightUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;

            shell.Visual.AddChild(backdrop);

            var sprite = new SpriteRuntime();
            sprite.X = 0;
            sprite.Y = 0;
            sprite.Width = 100f;
            sprite.WidthUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;
            sprite.Height = 100f;
            sprite.HeightUnits = Gum.DataTypes.DimensionUnitType.PercentageOfParent;

            shell.Visual.AddChild(sprite);
            _viewportSprites[node.Id] = sprite;

            shell.Visual.Width = node.Width > 0f ? node.Width : 320f;
            shell.Visual.Height = node.Height > 0f ? node.Height : 180f;
            return shell;
        }

        // ── Per-frame updates ────────────────────────────────────────────────

        private void Update(UINode node, FrameworkElement control)
        {
            switch (node.Kind)
            {
                case UIWidgetKind.Canvas when control is Panel canvas:
                    UpdateCanvas(node, canvas);
                    break;

                case UIWidgetKind.Label when control is Label label:
                    label.Text = node.Text;
                    StyleText(label, node);
                    break;

                case UIWidgetKind.Button when control is Button button:
                    button.Text = node.Text;
                    StyleTextTree(button.Visual, node);
                    break;

                case UIWidgetKind.Toggle:
                case UIWidgetKind.Checkbox:
                    if (control is CheckBox check)
                    {
                        check.Text = node.Text;
                        check.IsChecked = node.Bool;
                        StyleTextTree(check.Visual, node);
                    }
                    break;

                case UIWidgetKind.Slider when control is Slider slider:
                    slider.Minimum = node.Min;
                    slider.Maximum = node.Max;
                    slider.Value = node.Value;
                    break;

                case UIWidgetKind.TextInput when control is TextBox textBox:
                    textBox.Text = node.Text;
                    if (!string.IsNullOrEmpty(node.Placeholder)) textBox.Placeholder = node.Placeholder;
                    // Only recolor real input: the placeholder keeps its theme
                    // ghost color while the field is empty.
                    if (!string.IsNullOrEmpty(node.Text)) StyleTextTree(textBox.Visual, node);
                    break;

                case UIWidgetKind.Rect when control is Panel rectPanel:
                    UpdateRect(node, rectPanel);
                    break;

                case UIWidgetKind.Image when control is Image image:
                    SetImageSource(node.Id, image, node.ImageSource);
                    break;

                case UIWidgetKind.Icon when control is Image iconImage:
                    SetImageSource(node.Id, iconImage, node.ImageSource);
                    break;

                case UIWidgetKind.Icon when control is Label glyph:
                    glyph.Text = node.ImageSource;
                    StyleText(glyph, node);
                    break;

                case UIWidgetKind.ProgressBar when control is Panel progress:
                    UpdateProgressBar(node, progress);
                    break;

                case UIWidgetKind.Viewport when control is Panel viewport:
                    UpdateViewport(node, viewport);
                    break;
            }

            if (node.Kind != UIWidgetKind.Canvas)
                ApplySize(node, control);

            // An anchor (from UIStyleComponent.Anchor) positions the control within its
            // parent (e.g. "center"), and opts it out of the flow layout below.
            if (TryGetAnchor(node.Anchor, out var anchor))
                control.Anchor(anchor);
        }

        private static void ApplySize(UINode node, FrameworkElement control)
        {
            if (node.Width > 0f)
            {
                control.Visual.Width = node.Width;
                control.Visual.WidthUnits = Gum.DataTypes.DimensionUnitType.Absolute;
            }
            if (node.Height > 0f)
            {
                control.Visual.Height = node.Height;
                control.Visual.HeightUnits = Gum.DataTypes.DimensionUnitType.Absolute;
            }
        }

        /// <summary>
        /// Canvases with an explicit size become positioned overlays: anchored at
        /// (AnchorX, AnchorY) within the Gum canvas (both 0..1, default 0.5 = centered).
        /// Unsized canvases keep the Fill dock applied at creation.
        /// </summary>
        private static void UpdateCanvas(UINode node, Panel canvas)
        {
            if (node.Width > 0f && node.Height > 0f)
            {
                canvas.Visual.Width = node.Width;
                canvas.Visual.WidthUnits = Gum.DataTypes.DimensionUnitType.Absolute;
                canvas.Visual.Height = node.Height;
                canvas.Visual.HeightUnits = Gum.DataTypes.DimensionUnitType.Absolute;

                canvas.Visual.X = Math.Max(0f, (GumService.Default.CanvasWidth - node.Width) * Clamp01(node.AnchorX));
                canvas.Visual.Y = Math.Max(0f, (GumService.Default.CanvasHeight - node.Height) * Clamp01(node.AnchorY));
            }
        }

        private static void UpdateRect(UINode node, Panel shell)
        {
            foreach (var child in shell.Visual.Children)
            {
                if (child is Gum.GueDeriving.RectangleRuntime rect)
                {
                    rect.FillColor = ParseColor(node.Color) ?? DefaultRectColor;
                    rect.CornerRadius = node.CornerRadius;
                }
            }
        }

        private static void UpdateProgressBar(UINode node, Panel shell)
        {
            foreach (var child in shell.Visual.Children)
            {
                if (child is not Gum.GueDeriving.ColoredRectangleRuntime track) continue;

                foreach (var trackChild in track.Children)
                {
                    if (trackChild is not Gum.GueDeriving.ColoredRectangleRuntime fill) continue;

                    float range = node.Max - node.Min;
                    float ratio = range > 0f ? Clamp01((node.Value - node.Min) / range) : Clamp01(node.Value);

                    if (node.Bool)
                    {
                        // Indeterminate: full-width, dimmed pulse-free placeholder.
                        fill.Width = 100f;
                        fill.Alpha = 140;
                    }
                    else
                    {
                        fill.Width = ratio * 100f;
                        fill.Alpha = 255;
                    }

                    fill.Color = ParseColor(node.Color)
                        ?? StyleHintColor(node.StyleHint)
                        ?? AccentColor;
                }
            }
        }

        private static void UpdateViewport(UINode node, Panel shell)
        {
            foreach (var child in shell.Visual.Children)
            {
                if (child is Gum.GueDeriving.RectangleRuntime backdrop)
                    backdrop.FillColor = ParseColor(node.Color) ?? DefaultViewportColor;
            }
        }

        /// <summary>Apply text color (node.Color, then style-hint fallback) and font size.</summary>
        private static void StyleText(Label label, UINode node)
        {
            if (label.TextComponent is not Gum.GueDeriving.TextRuntime text) return;
            ApplyTextColor(text, node);

            if (node.FontSize > 0f)
                text.FontScale = node.FontSize / DefaultBaseFontSize;
        }

        /// <summary>
        /// Recolor every text runtime under a composite widget (Button, CheckBox,
        /// TextBox shells). Gum's V3 theme defaults to pure-white text, which reads
        /// neon against the dark editor surfaces — everything falls back to a
        /// softer default unless the node carries its own color or style hint.
        /// </summary>
        private static void StyleTextTree(Gum.Wireframe.GraphicalUiElement visual, UINode node)
        {
            foreach (var child in visual.Children)
            {
                if (child is Gum.GueDeriving.TextRuntime tr) ApplyTextColor(tr, node);
                StyleTextTree(child, node);
            }
        }

        private static readonly Color DefaultTextColor = new(0xd6, 0xd9, 0xde);

        private static void ApplyTextColor(Gum.GueDeriving.TextRuntime text, UINode node)
        {
            text.Color = ParseColor(node.Color) ?? StyleHintColor(node.StyleHint) ?? DefaultTextColor;
        }

        private void SetImageSource(long nodeId, Image image, string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            if (_imageSources.TryGetValue(nodeId, out var applied) && applied == source) return;
            try
            {
                image.Source = source;
                _imageSources[nodeId] = source;
            }
            catch (Exception e)
            {
                // Don't retry every frame after a load failure, but allow a different
                // source to be applied later.
                _imageSources[nodeId] = source;
                Console.WriteLine($"[GumUIRenderer] failed to load image '{source}': {e.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static bool LooksLikeTexturePath(string source) =>
            !string.IsNullOrEmpty(source) &&
            (source.Contains('/') || source.Contains('\\') ||
             source.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
             source.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
             source.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
             source.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
             source.EndsWith(".gif", StringComparison.OrdinalIgnoreCase));

        /// <summary>Parse "#rrggbb", "#aarrggbb" (or without '#'); null when not a hex color.</summary>
        private static Color? ParseColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var hex = value.Trim().TrimStart('#');
            if (hex.Length == 6)
            {
                if (TryParseHexBytes(hex, out var r, out var g, out var b))
                    return new Color(r, g, b, (byte)255);
            }
            else if (hex.Length == 8)
            {
                // #AARRGGBB
                string rgb = hex.Substring(2);
                if (TryParseHexBytes(rgb, out var r, out var g, out var b) &&
                    byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var a))
                    return new Color(r, g, b, a);
            }
            return null;
        }

        private static bool TryParseHexBytes(string rgb, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            return byte.TryParse(rgb.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
                && byte.TryParse(rgb.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
                && byte.TryParse(rgb.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
        }

        /// <summary>Theme mapping for <c>UIStyleComponent.StyleHint</c>; null when unmapped.</summary>
        private static Color? StyleHintColor(string? hint)
        {
            switch (hint?.Trim().ToLowerInvariant())
            {
                case "accent": return AccentColor;
                case "danger": return new Color(0xe5, 0x48, 0x4d);
                case "muted": return new Color(0x9a, 0x9a, 0x9a);
                case "selected": return new Color(0x7f, 0xd7, 0xa4);
                case "title": return Color.White;
                default: return null;
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static bool TryGetAnchor(string value, out Gum.Wireframe.Anchor anchor)
        {
            anchor = Gum.Wireframe.Anchor.TopLeft;
            if (string.IsNullOrWhiteSpace(value)) return false;

            switch (value.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", ""))
            {
                case "center":
                case "centre":
                case "middle": anchor = Gum.Wireframe.Anchor.Center; return true;
                case "top": anchor = Gum.Wireframe.Anchor.Top; return true;
                case "topleft": anchor = Gum.Wireframe.Anchor.TopLeft; return true;
                case "topright": anchor = Gum.Wireframe.Anchor.TopRight; return true;
                case "left": anchor = Gum.Wireframe.Anchor.Left; return true;
                case "right": anchor = Gum.Wireframe.Anchor.Right; return true;
                case "bottom": anchor = Gum.Wireframe.Anchor.Bottom; return true;
                case "bottomleft": anchor = Gum.Wireframe.Anchor.BottomLeft; return true;
                case "bottomright": anchor = Gum.Wireframe.Anchor.BottomRight; return true;
                default: return false;
            }
        }

        // ── Layout ───────────────────────────────────────────────────────────
        // Two-pass measure/arrange over the UI node tree. Honors
        // LayoutElementComponent (Min/Preferred/Flexible), VLayout/HLayout/Splitter
        // spacing+padding+Expand, and stretches children across the non-main axis
        // (Godot VBox/HBox default). Cross-axis stretch, main-axis pack with
        // flex distribution for leftover space.

        private struct Size2 { public float W, H; }

        /// <summary>Intrinsic (preferred) size of a node when nothing else says otherwise.</summary>
        private static Size2 Intrinsic(UINode n)
        {
            float fs = n.FontSize > 0f ? n.FontSize : 16f;
            switch (n.Kind)
            {
                case UIWidgetKind.Label:
                    return new Size2 { W = n.Text.Length * (fs * 0.52f) + 8f, H = fs * 1.5f + 6f };
                case UIWidgetKind.Button:
                    return new Size2 { W = n.Text.Length * (fs * 0.55f) + 28f, H = fs * 1.4f + 12f };
                case UIWidgetKind.TextInput:
                    return new Size2 { W = 150f, H = 30f };
                case UIWidgetKind.Slider:
                    return new Size2 { W = 140f, H = 28f };
                case UIWidgetKind.Toggle:
                case UIWidgetKind.Checkbox:
                    return new Size2 { W = 24f + n.Text.Length * (fs * 0.52f), H = 28f };
                case UIWidgetKind.ProgressBar:
                    return new Size2 { W = 140f, H = 18f };
                case UIWidgetKind.Image:
                case UIWidgetKind.Icon:
                    return new Size2 { W = n.Width > 0f ? n.Width : 64f, H = n.Height > 0f ? n.Height : 64f };
                case UIWidgetKind.Rect:
                    return new Size2 { W = n.Width > 0f ? n.Width : 100f, H = n.Height > 0f ? n.Height : 100f };
                case UIWidgetKind.Viewport:
                    return new Size2 { W = n.Width > 0f ? n.Width : 320f, H = n.Height > 0f ? n.Height : 180f };
                default:
                    return new Size2 { W = 100f, H = 30f };
            }
        }

        private Size2 Measure(UINode n, Dictionary<long, List<UINode>> children)
        {
            switch (n.Kind)
            {
                case UIWidgetKind.VLayout:
                case UIWidgetKind.Scroll:
                case UIWidgetKind.Tree:
                case UIWidgetKind.Panel:
                case UIWidgetKind.Viewport:
                {
                    float w = 0f, h = 0f;
                    float pad = n.Padding > 0f ? n.Padding : 0f;
                    float sp = n.Spacing > 0f ? n.Spacing : (n.Kind == UIWidgetKind.Panel || n.Kind == UIWidgetKind.Viewport ? 0f : 6f);
                    if (children.TryGetValue(n.Id, out var kids))
                    {
                        foreach (var k in kids)
                        {
                            var s = PreferredSize(k, children);
                            w = Math.Max(w, s.W);
                            h += s.H;
                        }
                        if (kids.Count > 0) h += sp * (kids.Count - 1);
                    }
                    h += 2f * pad; w += 2f * pad;
                    if (w <= 0f) w = Intrinsic(n).W;
                    if (h <= 0f) h = Intrinsic(n).H;
                    return new Size2 { W = w, H = h };
                }
                case UIWidgetKind.HLayout:
                case UIWidgetKind.Splitter when !n.Vertical:
                {
                    float w = 0f, h = 0f;
                    float pad = n.Padding > 0f ? n.Padding : 0f;
                    float sp = n.Spacing > 0f ? n.Spacing : 6f;
                    if (children.TryGetValue(n.Id, out var kids) && kids.Count > 0)
                    {
                        foreach (var k in kids)
                        {
                            var s = PreferredSize(k, children);
                            w += s.W;
                            h = Math.Max(h, s.H);
                        }
                        w += sp * (kids.Count - 1);
                    }
                    w += 2f * pad; h += 2f * pad;
                    if (w <= 0f) w = Intrinsic(n).W;
                    if (h <= 0f) h = Intrinsic(n).H;
                    return new Size2 { W = w, H = h };
                }
                case UIWidgetKind.Splitter when n.Vertical:
                {
                    float w = 0f, h = 0f;
                    float pad = n.Padding > 0f ? n.Padding : 0f;
                    float sp = n.Spacing > 0f ? n.Spacing : 6f;
                    if (children.TryGetValue(n.Id, out var kids) && kids.Count > 0)
                    {
                        foreach (var k in kids)
                        {
                            var s = PreferredSize(k, children);
                            w = Math.Max(w, s.W);
                            h += s.H;
                        }
                        h += sp * (kids.Count - 1);
                    }
                    w += 2f * pad; h += 2f * pad;
                    if (w <= 0f) w = Intrinsic(n).W;
                    if (h <= 0f) h = Intrinsic(n).H;
                    return new Size2 { W = w, H = h };
                }
                default:
                    return Intrinsic(n);
            }
        }

        /// <summary>Preferred size for node: LayoutElement overrides beat intrinsic measurement.</summary>
        private Size2 PreferredSize(UINode n, Dictionary<long, List<UINode>> children)
        {
            var m = Measure(n, children);
            return new Size2
            {
                W = Math.Max(n.MinWidth > 0f ? n.MinWidth : 0f, n.PreferredWidth > 0f ? n.PreferredWidth : m.W),
                H = Math.Max(n.MinHeight > 0f ? n.MinHeight : 0f, n.PreferredHeight > 0f ? n.PreferredHeight : m.H),
            };
        }

        private void Arrange(UINode n, Rectangle rect, Dictionary<long, List<UINode>> children)
        {
            _lastRects[n.Id] = rect;
            if (_controls.TryGetValue(n.Id, out var control))
                SetBounds(control, rect);

            if (n.Kind == UIWidgetKind.Viewport)
            {
                _viewportRects[n.Id] = rect;
                _viewportColors[n.Id] = ParseColor(n.Color) ?? DefaultViewportColor;
            }

            if (!children.TryGetValue(n.Id, out var kids) || kids.Count == 0) return;

            bool horizontal = n.Kind == UIWidgetKind.HLayout
                || (n.Kind == UIWidgetKind.Splitter && !n.Vertical);
            bool fill = n.Kind == UIWidgetKind.Canvas;

            float pad = n.Padding > 0f ? n.Padding : 0f;
            float sp = n.Spacing > 0f ? n.Spacing : (horizontal || n.Kind == UIWidgetKind.VLayout || n.Kind == UIWidgetKind.Tree || n.Kind == UIWidgetKind.Splitter || n.Kind == UIWidgetKind.Scroll ? 6f : 0f);
            if (n.Kind == UIWidgetKind.Panel || n.Kind == UIWidgetKind.Viewport) sp = n.Spacing;

            if (fill)
            {
                // Canvas children stretch to the whole canvas (Root expands to fill).
                foreach (var k in kids)
                {
                    _nested[k.Id] = false;
                    Arrange(k, rect, children);
                }
                return;
            }

            float main = horizontal ? rect.Width - 2f * pad : rect.Height - 2f * pad;
            float cross = horizontal ? rect.Height - 2f * pad : rect.Width - 2f * pad;

            // Scroll offset: clamp the stored wheel offset against the content
            // measured below, then shift the flow start so overflow slides under
            // the panel's clip. Only vertical scrolling is supported.
            float scrollOff = 0f;
            if (n.Kind == UIWidgetKind.Scroll && !horizontal)
            {
                float contentH = 0f;
                int flowCount = 0;
                foreach (var k in kids)
                {
                    if (TryGetAnchor(k.Anchor, out _)) continue;
                    contentH += PreferredSize(k, children).H;
                    flowCount++;
                }
                if (flowCount > 0) contentH += sp * (flowCount - 1);
                _scrollContentH[n.Id] = contentH;
                _scrollOffsets.TryGetValue(n.Id, out scrollOff);
                float maxOff = Math.Max(0f, contentH - Math.Max(0f, main));
                if (scrollOff < 0f) scrollOff = 0f;
                if (scrollOff > maxOff) scrollOff = maxOff;
                _scrollOffsets[n.Id] = scrollOff;
            }

            // Base sizes along the main axis; flex shares leftover space.
            // A Scroll box takes its SIZED share (explicit pref/min + flex),
            // never its content height — taller content scrolls inside it.
            // (An unsized, non-flex Scroll collapses to zero: a scroll region
            // must be given a size to be useful.)
            float total = 0f;
            var bases = new Dictionary<long, float>();
            float flexSum = 0f;
            foreach (var k in kids)
            {
                if (TryGetAnchor(k.Anchor, out _)) continue;
                var pref = PreferredSize(k, children);
                float b = horizontal ? pref.W : pref.H;
                if (k.Kind == UIWidgetKind.Scroll)
                {
                    float explPref = horizontal ? k.PreferredWidth : k.PreferredHeight;
                    float explMin = horizontal ? k.MinWidth : k.MinHeight;
                    b = Math.Max(explMin > 0f ? explMin : 0f, explPref > 0f ? explPref : 0f);
                }
                bases[k.Id] = b;
                total += b;
                flexSum += FlexFor(k, horizontal);
            }

            float free = main - total - sp * Math.Max(0, kids.Count - 1);
            float cursor = (horizontal ? rect.X + pad : rect.Y + pad) - scrollOff;

            // Children of a Scroll (or of an already-nested container) are
            // nested visuals, so their bounds are relative to this panel —
            // absolute otherwise (flat attach at the Gum root).
            bool nestKids = n.Kind == UIWidgetKind.Scroll
                || (_nested.TryGetValue(n.Id, out var nestSelf) && nestSelf);

            foreach (var k in kids)
            {
                if (TryGetAnchor(k.Anchor, out _)) continue;
                float b = bases[k.Id];
                float f = FlexFor(k, horizontal);
                float size = b + (flexSum > 0f ? free * (f / flexSum) : 0f);
                // Never shrink below the explicit min or, for text widgets, the
                // text's own width/height (otherwise Gum wraps "Box" into "B/ox").
                size = Math.Max(size, MinAlong(k, horizontal));
                if (size < 0f) size = 0f;
                float crossSize = cross; // stretch across the non-main axis
                Rectangle kr = horizontal
                    ? new Rectangle((int)cursor, (int)(rect.Y + pad), Math.Max(0, (int)size), Math.Max(0, (int)crossSize))
                    : new Rectangle((int)(rect.X + pad), (int)cursor, Math.Max(0, (int)crossSize), Math.Max(0, (int)size));
                _nested[k.Id] = nestKids;
                Arrange(k, kr, children);
                if (_controls.TryGetValue(k.Id, out var kc) && nestKids)
                    SetBounds(kc, new Rectangle(kr.X - (int)rect.X, kr.Y - (int)rect.Y, kr.Width, kr.Height));
                cursor += size + sp;
            }
        }

        /// <summary>Smallest size a node may be squeezed to along the main axis.</summary>
        private static float MinAlong(UINode n, bool horizontal)
        {
            float explicitMin = horizontal ? n.MinWidth : n.MinHeight;
            if (explicitMin > 0f) return explicitMin;
            switch (n.Kind)
            {
                case UIWidgetKind.Button:
                case UIWidgetKind.Label:
                case UIWidgetKind.Checkbox:
                case UIWidgetKind.Toggle:
                    var s = Intrinsic(n);
                    return horizontal ? s.W : s.H;
                default:
                    return 0f;
            }
        }

        private static bool IsContainer(UIWidgetKind kind) =>
            kind == UIWidgetKind.Canvas || kind == UIWidgetKind.HLayout || kind == UIWidgetKind.VLayout
            || kind == UIWidgetKind.Splitter || kind == UIWidgetKind.Tree || kind == UIWidgetKind.Panel
            || kind == UIWidgetKind.Scroll || kind == UIWidgetKind.Viewport;

        private static float FlexFor(UINode n, bool horizontal)
        {
            float flex = horizontal ? n.FlexibleWidth : n.FlexibleHeight;
            if (flex > 0f) return flex;
            // VLayout/HLayout/Splitter/Scroll/Viewport Expand absorbs leftover along the parent's main axis.
            if (n.Expand && (n.Kind == UIWidgetKind.VLayout || n.Kind == UIWidgetKind.HLayout || n.Kind == UIWidgetKind.Splitter || n.Kind == UIWidgetKind.Scroll || n.Kind == UIWidgetKind.Viewport))
                return 1f;
            return 0f;
        }

        private void SetBounds(FrameworkElement control, Rectangle rect)
        {
            var v = control.Visual;
            v.XUnits = Gum.Converters.GeneralUnitType.PixelsFromSmall;
            v.YUnits = Gum.Converters.GeneralUnitType.PixelsFromSmall;
            // Gum's Dock() leaves Center/Center origins (seen on canvas panels),
            // which offsets the whole subtree by half the parent size. We position
            // everything in absolute canvas pixels, so pin top-left origins here.
            v.XOrigin = RenderingLibrary.Graphics.HorizontalAlignment.Left;
            v.YOrigin = RenderingLibrary.Graphics.VerticalAlignment.Top;
            v.WidthUnits = Gum.DataTypes.DimensionUnitType.Absolute;
            v.HeightUnits = Gum.DataTypes.DimensionUnitType.Absolute;
            v.X = rect.X;
            v.Y = rect.Y;
            v.Width = rect.Width;
            v.Height = rect.Height;
        }

        /// <summary>
        /// Flatten: every node's effective parent is its nearest CONTAINER
        /// ancestor (hierarchy tree row elements nest child rows inside a
        /// ButtonComponent-bearing element — Godot renders those as TreeItems,
        /// we flatten them into a stacked list). Canvas roots form the top.
        /// Nodes whose parent is missing from the frame are skipped instead of
        /// throwing, so one stale node can't take the whole UI down.
        /// </summary>
        private static Dictionary<long, List<UINode>> BuildChildrenMap(List<UINode> nodes, Dictionary<long, UINode> byId)
        {
            var children = new Dictionary<long, List<UINode>>();
            foreach (var node in nodes)
            {
                if (node.ParentId == 0) continue;
                if (!byId.TryGetValue(node.ParentId, out var effParent)) continue;
                while (!IsContainer(effParent.Kind))
                {
                    if (effParent.ParentId == 0 || !byId.TryGetValue(effParent.ParentId, out var up)) break;
                    effParent = up;
                }
                if (!IsContainer(effParent.Kind)) continue; // no container above it — undisplayable
                if (!children.TryGetValue(effParent.Id, out var list))
                    children[effParent.Id] = list = new List<UINode>();
                list.Add(node);
            }
            return children;
        }

        /// <summary>Top of the layout pass: clears per-viewport rects and arranges the tree.</summary>
        private void Layout(List<UINode> nodes, Dictionary<long, UINode> byId, Dictionary<long, List<UINode>> children)
        {
            _viewportRects.Clear();
            _viewportColors.Clear();
            _scrollIds.Clear();
            _nested.Clear();
            foreach (var node in nodes)
                if (node.Kind == UIWidgetKind.Scroll) _scrollIds.Add(node.Id);

            foreach (var node in nodes)
            {
                if (node.Kind != UIWidgetKind.Canvas) continue;
                if (!_controls.TryGetValue(node.Id, out var control)) continue;

                Rectangle rect;
                if (node.Width > 0f && node.Height > 0f)
                {
                    rect = new Rectangle((int)control.Visual.X, (int)control.Visual.Y, (int)node.Width, (int)node.Height);
                }
                else
                {
                    rect = new Rectangle(0, 0, (int)GumService.Default.CanvasWidth, (int)GumService.Default.CanvasHeight);
                }

                SetBounds(control, rect);
                Arrange(node, rect, children);
            }

            if (!_loggedLayoutDump)
            {
                _loggedLayoutDump = true;
                Console.WriteLine($"[GumUIRenderer] LAYOUT DUMP canvas={GumService.Default.CanvasWidth}x{GumService.Default.CanvasHeight} nodes={nodes.Count}");
                foreach (var node in nodes)
                {
                    string r = _lastRects.TryGetValue(node.Id, out var rc)
                        ? $"[{rc.X},{rc.Y} {rc.Width}x{rc.Height}]"
                        : "[NO-RECT]";
                    string txt = node.Text.Length > 24 ? node.Text[..24] + "…" : node.Text;
                    Console.WriteLine($"  id={node.Id} p={node.ParentId} {node.Kind} {r} src={node.Width}x{node.Height} pref={node.PreferredWidth}x{node.PreferredHeight} flex={node.FlexibleWidth}/{node.FlexibleHeight} exp={node.Expand} sp={node.Spacing} pad={node.Padding} txt=\"{txt}\"");
                }
                foreach (var kv in _viewportRects)
                    Console.WriteLine($"  VIEWPORT id={kv.Key} rect={kv.Value} color={_viewportColors[kv.Key]}");
                Console.WriteLine("[GumUIRenderer] VISUAL DUMP");
                foreach (var node in nodes)
                {
                    if (!_controls.TryGetValue(node.Id, out var c)) { Console.WriteLine($"  id={node.Id} {node.Kind} NO-CONTROL"); continue; }
                    var v = c.Visual;
                    string parent = v.Parent == null ? "null" : $"{v.Parent.GetType().Name}";
                    string porigin = v.Parent == null ? "-" : $"{v.Parent.XOrigin}/{v.Parent.YOrigin}/{v.Parent.ChildrenLayout}";
                    Console.WriteLine($"  id={node.Id} {node.Kind} ctrl={c.GetType().Name} xy=({v.X},{v.Y}) wh=({v.Width}x{v.Height}) abs=({v.AbsoluteX},{v.AbsoluteY} {v.AbsoluteWidth}x{v.AbsoluteHeight}) vis={v.Visible}/{v.AbsoluteVisible} parent={parent} kids={v.Children.Count} origin={v.XOrigin}/{v.YOrigin}/{v.XUnits}/{v.YUnits} porigin={porigin}");
                }
            }
        }
    }
}
