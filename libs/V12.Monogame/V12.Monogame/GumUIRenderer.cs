using System;
using System.Collections.Generic;
using Gum;
using Gum.Forms;
using Gum.Forms.Controls;
using Gum.Wireframe;
using Microsoft.Xna.Framework;
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
    /// Rect (colored rectangle), Image, Icon (glyph or texture), ProgressBar (track+fill) and
    /// Viewport (placeholder panel — see <see cref="CreateViewport"/>).
    /// </summary>
    public sealed class GumUIRenderer : IUIRenderer
    {
        private const float DefaultBaseFontSize = 16f;

        private static readonly Color DefaultTrackColor = new(0x2a, 0x2a, 0x2e);
        private static readonly Color DefaultRectColor = new(0x2b, 0x2b, 0x31);
        private static readonly Color DefaultViewportColor = new(0x1a, 0x1a, 0x20);
        private static readonly Color AccentColor = new(0x4c, 0xc2, 0xff);

        private readonly GameRoot _root;
        private readonly Dictionary<long, FrameworkElement> _controls = new();
        private readonly Dictionary<long, string> _imageSources = new();
        private bool _initialized;
        private bool _loggedFirstFrame;
        private bool _loggedWorldSpaceCanvas;
        private bool _loggedViewportPlaceholder;

        public GumUIRenderer(Game game, GameRoot root)
        {
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
        public void Update(GameTime gameTime) => GumService.Default.Update(gameTime);

        /// <summary>Draw the Gum UI (call from the host's Draw, after the 3D pass).</summary>
        public void Draw() => GumService.Default.Draw();

        // ── Reconciliation ───────────────────────────────────────────────────

        private void Reconcile(List<UINode>? nodes)
        {
            if (!_initialized) return;

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
                }
            }

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

                    if (node.ParentId != 0 && _controls.TryGetValue(node.ParentId, out var parent))
                        parent.AddChild(control);
                    else
                        control.AddToRoot();
                }

                Update(node, control);
            }

            Layout(nodes, byId);
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
                Console.WriteLine("[GumUIRenderer] viewport placeholder: rendering viewports as tinted panels (render-to-texture not implemented)");
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

            var hint = new Label { Text = "Viewport" };
            var hintText = hint.TextComponent as Gum.GueDeriving.TextRuntime;
            if (hintText != null)
            {
                hintText.Color = new Color(0.6f, 0.6f, 0.65f);
                hintText.FontScale = 0.8f;
            }
            hint.Anchor(Gum.Wireframe.Anchor.Center);
            shell.AddChild(hint);

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
                    break;

                case UIWidgetKind.Toggle:
                case UIWidgetKind.Checkbox:
                    if (control is CheckBox check)
                    {
                        check.Text = node.Text;
                        check.IsChecked = node.Bool;
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

            var color = ParseColor(node.Color) ?? StyleHintColor(node.StyleHint);
            if (color.HasValue) text.Color = color.Value;

            if (node.FontSize > 0f)
                text.FontScale = node.FontSize / DefaultBaseFontSize;
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

        /// <summary>Small stack layout: children flow vertically (H layouts horizontally) in their parent.</summary>
        private void Layout(List<UINode> nodes, Dictionary<long, UINode> byId)
        {
            var groups = new Dictionary<long, List<UINode>>();
            foreach (var node in nodes)
            {
                if (node.ParentId == 0) continue; // canvases are docked, not laid out
                if (!groups.TryGetValue(node.ParentId, out var list))
                    groups[node.ParentId] = list = new List<UINode>();
                list.Add(node);
            }

            foreach (var group in groups)
            {
                byId.TryGetValue(group.Key, out var parentNode);
                bool horizontal = parentNode?.Kind == UIWidgetKind.HLayout;
                float spacing = parentNode?.Spacing > 0f ? parentNode.Spacing : 6f;
                float padding = parentNode?.Padding > 0f ? parentNode.Padding : 8f;
                float cursor = padding;

                foreach (var child in group.Value)
                {
                    if (!_controls.TryGetValue(child.Id, out var control)) continue;
                    if (TryGetAnchor(child.Anchor, out _)) continue; // anchored children place themselves

                    if (horizontal)
                    {
                        control.Visual.X = cursor;
                        control.Visual.Y = padding;
                        cursor += (child.Width > 0f ? child.Width : 100f) + spacing;
                    }
                    else
                    {
                        control.Visual.X = padding;
                        control.Visual.Y = cursor;
                        cursor += (child.Height > 0f ? child.Height : 34f) + spacing;
                    }
                }
            }
        }
    }
}
