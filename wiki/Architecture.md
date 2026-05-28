# Architecture

The framework is small on purpose. Most tools are self-contained Unity Editor windows or build hooks, with shared code only where it prevents duplicated behavior across preview, upload, and explicit apply flows.

## Directory layout

```
Editor/
  AvatarQolLogger.cs                    package logger registration
  AvatarQolMenus.cs                     shared menu path constants
  AvatarQolCategoryColors.cs            domain color hints for issue rows
  Internal/                             bundled wk-core utilities, styling, pipeline, reflection
  Common/                               avatar intent sessions, preview controller, precompute helpers
  Geometry/                             reusable mesh triangle and spatial-query helpers
  WeightFixes/                          Weight Sanity Check detector, cache, and apply hook
  Clipping/                             Clipping Fixer facade, scan, apply, and surface helpers
  BoneMerger/                           build hook, operation, and intent inspector
  PhysBonePreset/                       durable preset intent support
  Tools/                                editor windows and menu entry points
Runtime/
  WeightFixes/                          editor-only intent component for weight fixes
  Clipping/                             editor-only intent component for clipping fixes
  BoneMerger/                           editor-only intent component for bone merging
  PhysBonePreset/                       editor-only intent component for PhysBone presets
```

`Editor/Internal/` is synced from wk-core and intentionally bundled into this package. Downstream packages carry their own internal copy so VCC cannot pair a new tool package with an older shared-core version.

## Tool registration

Tools register entry points via Unity's standard `[MenuItem]` attributes. The framework does not impose a registration API because the current surface is small enough that explicit menu methods are clearer.

Convention: every tool's top-level menu entry lives under `Tools/WhyKnot/wk-vrc-qol/<Name>...`. If the tool has a sensible right-click trigger, it also registers `GameObject/WhyKnot/wk-vrc-qol/<Action>...` with a validator that disables the item for unsupported selections.

Use components only when the user's intent would otherwise be lost by re-importing source assets or entering play/upload, such as generated mesh, weight, clipping, or preset fixes. Component inspectors should stay minimal: the component is durable storage, while the main user flow lives in an Avatar QoL window with clear labels, validation, and preview controls.

## Shared utilities

Most cross-tool helpers come from the bundled wk-core source under `WhyKnot.AvatarQol.Internal.*`:

- `Internal.Utilities.PathUtility`, `AvatarUtility`, `FbxMeshUtility`, `BlendShapeUtility`, `HumanoidSideMap`, and Undo/folder helpers.
- `Internal.Styling.WkStyles` and `WkTheme` for common IMGUI/UI Toolkit chrome.
- `Internal.Pipeline.*` for preview, generated-asset scope, NDMF integration, and SDK fallback hooks.
- `Internal.Reflection.*` for UIElements and serialized-object reflection helpers.

Package-specific shared code should stay outside `Internal/` only when it is tied to wk-vrc-qol behavior, such as `AvatarQolCategoryColors`, intent-session glue in `Editor/Common/`, or mesh math in `Editor/Geometry/`.

## HumanoidSideMap

`WhyKnot.AvatarQol.Internal.Utilities.HumanoidSideMap` is the reusable symmetry helper. Given an `Animator` with `isHuman == true`, it builds:

- A `Transform -> HumanBodyBones` reverse map, one entry per bound Humanoid bone.
- A `Transform -> BoneSide` cache, memoized on first query.
- The avatar's left sign in Hips local space, derived from the actual position of `LeftUpperLeg` relative to `Hips`.

Side resolution walks the parent chain of any queried Transform until it hits a Humanoid bone, then reads the side off the bone's name (`Left*` / `Right*` -> side; everything else, including Hips, Spine, Chest, Neck, and Head -> Center). The result is cached, so repeated lookups during a vertex walk are O(1) after the first hit.

`ClassifyWorldPosition(worldPos, centerMargin)` extends the same logic to a free-floating world position: transform into Hips local space, project onto the left axis, and return Left/Right/Center based on a configurable margin around the centerline.

## Weight Sanity Check heuristic

Three steps for each renderer:

1. **Bone tagging.** For every entry in `SkinnedMeshRenderer.bones`, query `HumanoidSideMap.GetSide(bone)`. Cache into a `BoneSide[]` parallel to the bones array. Skip the renderer entirely if it has no Left or no Right bones, such as a head-only mesh.

2. **Vertex classification.** Iterate `Mesh.vertices` in bind-pose mesh local space. For each vertex, transform to world via the renderer transform, then to Hips local space. Classify as Left/Right/Center.

3. **Cross-side detection.** Iterate weights via `Mesh.GetAllBoneWeights()` and `Mesh.GetBonesPerVertex()`, the modern many-bone-per-vertex API. For each weight above the floor, look up the bone's pre-computed side. If vertex side is Left/Right and the bone's side is the opposite, flag the issue.

Center-banded vertices are deliberately not flagged: it is normal for spine vertices to have small bleed from arm bones, and flagging them would produce too many false positives.

## Mesh Geometry Helpers

`Editor/Geometry/` holds reusable triangle math and spatial queries that active tools share:

- `MeshGeometry` owns triangle closest-point math, ray/triangle hits, normal reconstruction, and mesh/submesh extraction helpers.
- `MeshSpatialQueries` owns grid-backed nearest/projection queries for source/target mesh correspondence.

UV Texture Transfer uses those helpers through `UvTextureTransferCore`, while UV rasterization, padding, and texture sampling stay in `UvTextureRaster`.

Clipping Fixer keeps its public entrypoints on `ClippingFixer`, with scan logic in `ClippingFixer.Scan.cs`, weight-writing/apply logic in `ClippingFixer.Apply.cs`, and renderer snapshot plus triangle hash code in `ClippingFixer.Surface.cs`.

## Why bind-pose, not the live skinned mesh?

Skinning is what Weight Sanity Check validates, so using the deformed mesh would be circular. The bind-pose vertex position is also the only stable spatial input when a scene first opens and no animation has played. It is accurate enough to classify which side of the avatar a vertex belongs to.

Clipping checks are different: they build a renderer snapshot from the current skinned/bone state because the question is whether the current surface occupies the same space as another surface or a PhysBone motion envelope.

## Undo and SetDirty

Read-only tools never mutate project state. Tools that do mutate state wrap operations in:

```csharp
var group = Undo.GetCurrentGroup();
Undo.SetCurrentGroupName("Avatar QoL: ...");
try {
    Undo.RegisterCompleteObjectUndo(target, "...");
    // ... mutate ...
    EditorUtility.SetDirty(target);
    Undo.CollapseUndoOperations(group);
} catch {
    Undo.RevertAllInCurrentGroup();
    throw;
}
```

If a mesh comes from an imported model asset, clone it to `Assets/AvatarQol Generated/`, assign the clone to the renderer, then write into the clone. Imported FBX/OBJ/DAE/glTF sub-assets should not be edited in place because the importer can overwrite them.
