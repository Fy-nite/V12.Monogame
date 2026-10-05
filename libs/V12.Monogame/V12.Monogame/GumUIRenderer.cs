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
    /// </summary>
    public sealed class GumUIRenderer : IUIRenderer
    {
        private readonly GameRoot _root;
        private readonly Dictionary<long, FrameworkElement> _controls = new();
        private bool _initialized;
        private bool _loggedFirstFrame;

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

        private static FrameworkElement Create(UINode node)
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
                default:
                    // Panel / HLayout / VLayout / Rect / ProgressBar / Image / Icon (visuals later)
                    return new Panel();
            }
        }

        private static void Update(UINode node, FrameworkElement control)
        {
            switch (node.Kind)
            {
                case UIWidgetKind.Label when control is Label label:
                    label.Text = node.Text;
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
            }

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

            // An anchor (from UIStyleComponent.Anchor) positions the control within its
            // parent (e.g. "center"), and opts it out of the flow layout below.
            if (TryGetAnchor(node.Anchor, out var anchor))
                control.Anchor(anchor);
        }

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
