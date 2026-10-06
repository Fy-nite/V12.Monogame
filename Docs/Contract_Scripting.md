# Contract (`.ct`) Scripting in V12

V12 can run Contract scripts in three ways, all driven from the same
`V12.Bindings` assembly:

| Flavor | Where it runs | Entry points |
| --- | --- | --- |
| Element script (`ScriptComponent`) | Attached to one element | `on_init()`, `on_update(dt)`, `on_action(name, value)`, `on_interact()` |
| Component type (`ContractComponentRegistry`) | Reusable component attached to many elements | `constructor()`, `on_attach()`, `on_update(dt)`, `on_detach()` |
| Gamepack (`ContractGamepack` / `ContractV12Host`) | Entry point inside a `.v12pak` | `Main`, `OnUpdate(dt)` |

Inside every flavor the owning element is available as a `long` id via
`V12.Script.Owner()` (0 when none), and all engine bindings are under the
`V12.*` names below.

> **Important — builtins are not global.** As of the current Contract, the
> standard library (`IO`, `Math`, …) lives under the reserved `__builtin`
> root and must be imported:
>
> ```ct
> import __builtin.std;   // grants IO, Math, ...
> ```
>
> or use the fully-qualified form `__builtin.std.IO.Println(...)`. A script
> that references bare `IO` will fail to compile with
> `Undefined variable: 'IO'`.

## Element scripts (`ScriptComponent`)

A `ScriptComponent` holds either a source path or inline text:

```csharp
elems.AddComponent(new ScriptComponent { Source = "scripts/SpinElement.ct" });
// or
elems.AddComponent(new ScriptComponent { ScriptText = "fn on_init() { ... }" });
```

- The runtime is chosen by extension via `ScriptRuntimeRegistry`
  (`V12ScriptRuntimeRegistration.RegisterAll`): `.ct` → Contract, `.lua` →
  MoonSharp. MoonSharp is the default for extensionless inline sources.
- `Source` is resolved through the registered `IAssetResolver`; if none is
  set or it returns nothing, the literal path (relative to the working
  directory) is used.
- Top-level functions named `on_init`, `on_update`, and any name called via
  `CallEvent`/`Call` are invoked; `on_init` runs once at load.
- Hot reload: `ScriptSystem` watches the `scripts/` folders of mounted asset
  paths and calls `Reload()` on every initialized script when a `.ct`/`.lua`
  file changes.

```ct
import __builtin.std;

Contract Ticker {
    static ticks: int;
}

fn on_init() {
    Ticker.ticks = 0;
    IO.Println("[SpinElement] on_init");
    V12.Components.SetColor(V12.Script.Owner(), 0.35, 0.75, 1.0, 1.0);
}

fn on_update(dt: float) {
    Ticker.ticks += 1;
    if (Ticker.ticks > 120) {
        Ticker.ticks = 0;
        IO.Println("[SpinElement] on_update tick");
    }
}
```

## Component types (`ContractComponentRegistry`)

Compile every `.ct` file under `scripts/components/` at startup (working
directory first, then the executable directory — the first non-empty one
wins). Every class type a file declares is registered by name, and its
lifecycle hooks are driven while attached:

```ct
import __builtin.std;

Contract Spin {
    speed: float;
    angle: float;

    constructor() {
        this.speed = 90.0;
        this.angle = 0.0;
    }

    fn on_attach() {
        IO.Println("[Spin] on_attach");
    }

    fn on_update(dt: float) {
        this.angle = this.angle + this.speed * dt;
        if (this.angle > 360.0) {
            this.angle = this.angle - 360.0;
            IO.Println("[Spin] on_update (full turn)");
        }
        V12.Components.SetFloat(V12.Script.Owner(), "Transform", "RY", this.angle * 0.0174533);
    }

    fn on_detach() {
        IO.Println("[Spin] on_detach");
    }
}
```

- Instance state is plain fields accessed as `this.<field>`.
- Attach from C#:
  `ContractComponentRegistry.FromGameRoot().Attach(element, "Spin")`,
  or from a script: `V12.Components.AddContractComponent(id, "Spin")`.
- The registry installs a `FileSystemWatcher` on `scripts/components`; edits
  recompile all known files. **Note:** reload affects instances created
  *after* the reload — live instances keep the module they were built with.
- From C# you can read/write instance fields via
  `ContractComponent.GetFloat/SetFloat/GetInt/SetInt/GetBool/SetBool/GetString/SetString`.

## Gamepacks (`.v12pak`)

Files under `scripts/` in a `.v12pak` are compiled as `ContractGamepack`s by
the pak loader and run through the same two-phase gamepak lifecycle: `Main`
runs once at startup, `OnUpdate(dt)` ticks per frame. The host is
`ContractV12Host`; failed compiles raise `ContractCompileException` and a
failed reload keeps the previous module and running scene. On reload, root
elements spawned by the previous run are despawned before `Main` runs again.

## Bindings API

All bindings are `static` methods callable by their `V12.*` name. Element
references are stable `long` ids; `0` means "not found". Component types are
looked up by name, case-insensitively, with optional `Component` suffix
(e.g. `"Transform"`, `"MeshComponent"`).

### `V12.Script`

| Method | Description |
| --- | --- |
| `Owner(): long` | Id of the element this script/component is attached to (0 when none) |

### `V12.Log`

| Method | Description |
| --- | --- |
| `Info(msg)` / `Warn(msg)` / `Error(msg)` | Route to the engine's Serilog logger |

### `V12.World`

| Method | Description |
| --- | --- |
| `SpawnElement(name): long` | Spawn a root element, return its id |
| `SpawnElementAt(name, x, y, z): long` | Spawn + position |
| `SpawnChild(parentId, name): long` | Spawn under a parent |
| `Despawn(elementId): bool` | Remove an element |
| `HasElement(elementId): bool` | Existence check |
| `FindByName(name): long` | First matching name, or 0 |
| `GetName/SetName` | Element name |
| `SetPosition(id, x, y, z): bool` | Create/update transform |
| `GetPositionX/Y/Z(id): float` | Read position |
| `ElementCount(): int` | Root elements in the target world |
| `WorldName()` / `SelectedWorldName(): string` | Current world info |

### `V12.Components`

| Method | Description |
| --- | --- |
| `AddTransform(id, x, y, z): bool` | Add/reposition the transform |
| `SetPosition` | Alias of `AddTransform` |
| `AddMesh(id, shape, w, h, d): bool` | Add/update a mesh |
| `SetMeshShape(id, shape): bool` | Change shape only |
| `SetColor(id, r, g, b, a): bool` | Material colour (0–1) |
| `AddCollider(id, shape, w, h, d): bool` | Add/update a collider |
| `HasComponent(id, typeName): bool` | Component presence |
| `RemoveComponent(id, typeName): bool` | Remove first match |
| `ComponentNames(id): string` | Comma-separated type names |
| `SetActive/IsActive` | Toggle element activity |
| `AddContractComponent(id, typeName): bool` | Attach a `.ct` component type |
| `ContractComponentTypes(): string` | Comma-separated registered type names |
| `AttachScript(id, source): bool` | Attach a `ScriptComponent` (`.ct`/`.lua`) |
| `AddComponent(id, typeName): bool` | Generic ensure-by-name (`Transform`, `Mesh`, `Collider`, `Material`, `Script`, or a `.ct` type) |
| `GetFloat/SetFloat(id, typeName, field): float/bool` | Read/write a float property (e.g. `SetFloat(id, "Transform", "RY", 45.0)`) |
| `GetString/SetString` | String property access |
| `GetBool/SetBool` | Bool property access |
| `ElementIdsCsv(): string` | All element ids |
| `ChildrenCsv(id): string` | Child ids of an element |

`shape` for meshes/colliders: `0=Box, 1=Sphere, 2=Capsule, 3=Cylinder, 4=Plane`.

### `V12.Registry`

| Method | Description |
| --- | --- |
| `Has(name): bool` | Service presence |
| `Register(name, long)` / `RegisterDouble(name, double)` / `RegisterString(name, string)` | Register/replace a service value |
| `GetInt(name): long` | Read a numeric service |
| `GetString(name): string` | Read a service's string form |
| `Remove(name): bool` | Unregister |
| `List(): string` | Newline-separated service names |

## Practical notes

- Compile errors are logged, not thrown: element scripts print
  `[ScriptComponent] Error initializing` / `[Contract] Compile error …`,
  component types print `[ContractComponents] Compile error in Spin.ct:` and
  the type stays unregistered (`No component type named 'Spin'`).
- All `.ct` files are copied to the build output (`scripts/**` in
  `Scramble.csproj`), so run the game from a directory where `scripts/`
  resolves — or rely on the executable-directory fallback.
- `ObjectRT` runs the scripts; the same `ContractRuntime` hosts both the
  stdlib bindings and the registered `V12.*` binding assemblies, so `IO` and
  `V12.*` calls resolve at runtime once compilation succeeds.
