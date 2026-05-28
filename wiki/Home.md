# wk-vrc-qol Wiki

Editor tools for VRChat avatars -- catches subtle issues that don't surface until they're already in your scene. Sibling repo to [wk-vrcfury-qol](https://github.com/RealWhyKnot/wk-vrcfury-qol): where wk-vrcfury-qol lives next to VRCFury components, this repo is for general avatar QoL -- meshes, weights, bones, materials.

The [README](https://github.com/RealWhyKnot/wk-vrc-qol/blob/main/README.md) is the quick-start; this wiki goes deeper.

## How it works (60 seconds)

The framework is small on purpose. Tools are mostly independent Unity Editor windows and build hooks that use Unity's public APIs (Animator/Humanoid, SkinnedMeshRenderer, Mesh), with shared infrastructure pulled from a bundled `Editor/Internal/` copy of wk-core.

Most reusable helpers live under `WhyKnot.AvatarQol.Internal.*`: logging, styling, path utilities, Humanoid side classification, generated-asset scope, and preview/build pipeline glue. Package-specific shared pieces stay narrow: `Editor/Common/` owns avatar intent sessions and preview control, while `Editor/Geometry/` owns reusable mesh triangle and spatial-query math.

Each tool picks what it needs from those helpers and otherwise keeps its scan/apply/window code local to the tool.

## Read these first

- **[[Installation]]** -- drop-in instructions
- **[[Tools-Overview]]** -- every shipping tool, what it does, where to find it
- **[[Architecture]]** -- framework design + how each heuristic works
- **[[Adding-a-Tool]]** -- developer guide
- **[[Troubleshooting]]** -- common failure modes and false-positive scenarios

## What's in the box

- **Weight Sanity Check** -- flags vertices on one side of a humanoid avatar that have non-trivial weight from a bone on the other side. Catches the most common Blender weight-transfer mistake.
- **PhysBone Clipping Risks** -- reviews likely PhysBone mesh clipping against selected meshes.
- **Auto Mesh Fixes** -- stores nondestructive clothing fit fixes and previews generated blendshape output.
- **Mask Painter** -- paints mask textures directly on avatar meshes in Scene view.
- **UV Texture Transfer** -- bakes texture colors from one UV/layout mesh to another through mesh correspondence.
- **Bone Merger** -- collapses duplicate or stray rig bones onto the intended bone.
- **PhysBone Preset** -- builds adaptive PhysBone setups from selected bone chains.

More tools to come.
