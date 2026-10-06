# How the V12 UI System Reaches the Renderer

This document explains how V12's declarative UI (`CanvasComponent` and friends) gets to a
renderer, and contrasts the minimal `MonogameV12Renderer` in this repo against the full
reference implementation in **Nova** (`V12TwoDog`).

The short version: **UI is now handed over explicitly.** V12 captures the UI into a
`UIFrame` and pushes it to a registered `IUIRenderer` (`ApplyUI`), which reconciles its own
controls by element id. A renderer *may* still walk the live tree itself (the older pull
model, still used by Nova below), but the engine now provides a push channel too.

---

## 1. What the "UI system" actually is

The UI system is a set of **declarative components** — they *describe* UI, they do not draw
it. All live under `V12.Components.UI.*` and derive from `ComponentBase` (they are **not**
`IRenderable`):

- `CanvasComponent` — root of a UI surface. The one entry point a host looks for.
- `ButtonComponent`, `LabelComponent`, `ToggleComponent`, `CheckboxComponent`,
  `SliderComponent`, `ProgressBarComponent`, `TextInputComponent`, `ImageComponent`,
  `IconComponent`, `RectComponent`
- `HLayoutComponent`, `VLayoutComponent`, `LayoutElementComponent`, `UIStyleComponent`
- `ViewportComponent`, `SplitterComponent`, `TreeComponent`

Children of a canvas element do **not** need their own `CanvasComponent`; they render as
part of the parent canvas. A child that has one becomes an independent canvas.

Source: `libs/V12.Basic/libs/V12/Components/UI/`, docs at
`libs/V12.Basic/libs/V12/Docs/UI_OVERVIEW.md`.

---

## 2. The two delivery channels in `GameRoot`

`libs/V12.Basic/libs/V12/Core/GameRoot.cs` offers two ways a renderer can receive work,
and **neither carries UI data**:

### a. `RenderPacket` — push path (legacy)

- `V12Tick` builds a `RenderPacket` from `GetAllRenderables()` and calls
  `IRenderer.QueueItems(packet)`.
- The packet (`Core/Interfaces/Renderer/RenderPackets.cs`) holds only
  `MeshDraw` / `SpriteDraw` / `TextDraw` lists.
- UI components are not `IRenderable`, so they are never included.

### b. `FrameSnapshot` — snapshot path

- `GameRoot.CaptureFrame()` walks the worlds and produces a `FrameSnapshot`
  (`Core/Rendering/FrameSnapshot.cs`) via `CaptureWorldFrame` → `EmitSnapshot`.
- `RenderableSnapshot` carries mesh / light / camera / sprite / SVG / text fields **only**.
  There are no canvas or widget fields.
- Because UI components are not `IRenderable`, they do not even produce a snapshot node.

So UI is absent from both channels.

### c. `IUIRenderer` + `UIFrame` — the UI push channel (added)

V12 now **hands UI over** instead of expecting the renderer to scout for it:

- `GameRoot.CaptureUI()` walks `ActiveWorlds` (plus the registered `UIBuilder` root) for
  `CanvasComponent` elements and emits a `UIFrame` (`Core/Rendering/UIFrame.cs`): one
  `UINode` per canvas/widget, keyed by element id (`UINode.Id`), carrying widget data and
  interaction sinks (`OnClick`, `OnValueChanged`, `OnToggled`, `OnTextChanged`) bound to the
  live components.
- `GameRoot.V12Tick` calls `uiRenderer.ApplyUI(frame)` under the same `_renderDirty` gate as
  the mesh packet. The backend is resolved from `Registry.Get("IUIRenderer")` in
  `Initialize()`.
- `IUIRenderer` (`Core/Interfaces/Renderer/IUIRenderer.cs`) = `ApplyUI(UIFrame)` + `step()`.

A backend reconciles create/update/destroy by id (the same style as the 3D snapshot path)
rather than scanning the tree. Screen-space canvases are the first-class case today;
world-space canvases are still deferred.

---

## 3. How UI is *meant* to reach a renderer

Per `UI_OVERVIEW.md`, the host renderer **walks the live V12 element tree** each frame:

1. Find elements bearing a `CanvasComponent`.
2. Render their child widgets with whatever backend the host uses (Godot `Control`s,
   Paper, ImGui, …).
3. World-space canvases render into a `RenderTexture`/`SubViewport` applied to a 3D quad;
   screen-space canvases render as an overlay.
4. Interaction is routed back to the components (e.g. a button press invokes the
   element's `ButtonComponent.OnClick`).

That is the Locus/Godot `WorldCanvasSystem.SyncFromV12()` model the doc describes. It is a
**pull** model: the renderer reaches into `GameRoot`, not the other way around.

---

## 4. This repo (`V12.SampleGame`) today

- There is **no** `WorldCanvasSystem` implementation here — the name appears only in docs
  and comments.
- `MonogameV12Renderer` (`libs/V12.Monogame/V12.Monogame/MonogameV12Renderer.cs`):
  - Consumes only the legacy `RenderPacket` meshes in `DrawFrame`.
  - Uses `ApplySnapshot` **solely** for camera resolution (`ResolveCamera`).
  - Has no node reconciliation, no interpolation, and no 3D UI pass.
- `GumUIRenderer` (`libs/V12.Monogame/V12.Monogame/GumUIRenderer.cs`), registered as
  `"IUIRenderer"`, implements the push channel: it receives `UIFrame`s and reconciles Gum
  Forms controls by element id (screen-space canvases only for now), invoked from `V12Game`.

Net result: 3D meshes render through `RenderPacket`; UI renders through the new
`IUIRenderer` push channel (Gum). The live-tree walk is no longer required.

---

## 5. Reference implementation: Nova (`G:\git\EcoVR\libs\nova`)

Nova is the in-development Godot 4.7 renderer/frontend for V12 (`V12TwoDog`). It shows the
complete pattern.

### Thread model (`Nova.Shared/V12Runtime.cs`)

- **V12 worker thread** (60Hz): `Root.Update(dt)`; if `ConsumeRenderDirty()`, calls
  `Root.CaptureFrame()` and enqueues the `FrameSnapshot` on a `ConcurrentQueue`.
- **Godot main thread** (`ProcessPhysics`): drains the queue into `_latestFrame`, then:
  1. `RendererService.ApplySnapshot(_latestFrame)` — 3D scene
  2. `AudioPlayer.ApplySnapshot(_latestFrame)` — audio
  3. `_worldCanvas.Update(Root)` — UI

Threads communicate only via the snapshot queue; all Godot node mutation happens on the
main thread.

### 3D path (`Nova.Shared/Renderer.cs`, implements `IRenderer`)

- `ApplySnapshot` reconciles Godot nodes from `RenderableSnapshot`s:
  - **Pass 1**: create missing nodes (lights, meshes, sprite/SVG `Sprite3D`, `Label3D`,
    `Camera3D`, `Node3D`).
  - **Pass 2**: parent + apply local transforms.
  - **Interpolation**: glide between 60Hz keyframes (`NodeAnim`), snapping on teleports.
  - **Fast path**: skip nodes whose snapshot is unchanged (`RenderableSnapshotEquals`).
  - **Eviction**: free nodes absent from the snapshot.
- `QueueItems` / `RemoveItems` **throw `NotImplementedException`** — Nova is snapshot-only
  and ignores the legacy `RenderPacket` that `MonogameV12Renderer` still uses.

### UI path (`Nova.Shared/WorldCanvasSystem.cs`)

This is the answer to "how does the UI system get sent to the renderer": **it isn't sent.**
`WorldCanvasSystem.Update(root)` walks the live element tree:

- Iterates `root.ActiveWorlds`, takes each world's read lock, and recurses via
  `CollectCanvases` looking for `CanvasComponent` elements.
- Reconciles a `CanvasNode` per canvas and a `WidgetNode` per widget, keyed by element id
  (**create / update / destroy**, the same reconciliation style as the 3D path).
- `KindOf(el)` maps component type → widget kind; `BuildVisuals` constructs real Godot
  `Control`s: `Label`, `Button`, `ColorRect`, `ProgressBar`, `CheckButton`/`CheckBox`,
  `HSlider`, `LineEdit`, `HBoxContainer`/`VBoxContainer`, `HSplitContainer`/
  `VSplitContainer`, `Tree`, `SubViewport`.
- **World-space** canvases (`ScreenSpace == false`): widgets render into a `SubViewport`
  whose texture is applied to a quad placed at the element's world transform.
- **Screen-space** canvases (`ScreenSpace == true`): widgets render into a `CanvasLayer`
  overlay.
- **Interaction**:
  - World-space: aim ray raycast against canvas quads (`RaycastWorldCanvases`), then Godot
    `InputEvent`s are pushed into the hit `SubViewport` so Godot's own `Control` hit-testing
    drives hover/press.
  - Screen-space: widgets take normal mouse/keyboard input.
  - Clicks resolve to `InvokeClick` → `ButtonComponent.InvokeClick()`.

### How the 3D and UI paths talk: `ViewportId`

They never share UI data. Coupling is purely by element id:

- A widget with a `ViewportComponent` calls
  `_renderer.RegisterSceneViewport(el.Id, subViewport)` (and `_gameRoot.MarkRenderDirty()`).
- In `Renderer.ApplySnapshot`, any renderable whose snapshot `ViewportId` matches that id is
  reparented **into** the SubViewport.

So 3D content renders *inside* UI-owned viewports (e.g. an editor GameView panel) without
the UI data ever crossing the 3D pipeline.

---

## 6. Side-by-side

| Concern | `MonogameV12Renderer` (this repo) | Nova `Renderer` + `WorldCanvasSystem` |
|---|---|---|
| Input channel | `RenderPacket` (meshes only) | `FrameSnapshot` queue (snapshot-only) |
| Node reconciliation | none | create/update/destroy by element id |
| Keyframe interpolation | none | `NodeAnim` glide |
| UI rendering | none | `WorldCanvasSystem` walks live tree |
| Screen-space UI | none | `CanvasLayer` overlay |
| World-space UI | none | `SubViewport` → quad |
| UI input | none | raycast → pushed Godot events |
| 3D↔UI link | none | snapshot `ViewportId` → scene viewport |

---

## 7. Key files

**This repo**
- `libs/V12.Basic/libs/V12/Core/GameRoot.cs` — `V12Tick`, `GetAllRenderables`, `CaptureFrame`
- `libs/V12.Basic/libs/V12/Core/Rendering/FrameSnapshot.cs` — `RenderableSnapshot`, `FrameSnapshot`
- `libs/V12.Basic/libs/V12/Core/Interfaces/Renderer/RenderPackets.cs` — `RenderPacket`
- `libs/V12.Basic/libs/V12/Core/Interfaces/Renderer/IRenderer.cs` — renderer contract
- `libs/V12.Basic/libs/V12/Components/UI/` — UI components
- `libs/V12.Basic/libs/V12/Docs/UI_OVERVIEW.md` — UI model + host pipeline
- `libs/V12.Monogame/V12.Monogame/MonogameV12Renderer.cs` — minimal renderer

**Nova (`G:\git\EcoVR\libs\nova`)**
- `Nova.Shared/V12Runtime.cs` — worker thread, snapshot queue, main-thread pump
- `Nova.Shared/Renderer.cs` — `IRenderer`, snapshot → Godot scene
- `Nova.Shared/WorldCanvasSystem.cs` — live-tree UI reconciliation
- `Docs/V12Renderer.md` — renderer overview
