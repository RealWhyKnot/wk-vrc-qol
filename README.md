# wk-vrc-qol

[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)
[![Unity](https://img.shields.io/badge/Unity-2022.3-000000.svg?logo=unity)](https://unity.com/)

Editor tools for VRChat avatars: catches subtle issues that don't surface until they're already in your scene. Sibling repo to [wk-vrcfury-qol](https://github.com/RealWhyKnot/wk-vrcfury-qol): where wk-vrcfury-qol lives next to VRCFury components, this repo is for general avatar QoL (meshes, weights, bones, materials).

The framework is small on purpose. Everything is built on Unity's public APIs (Animator/Humanoid, SkinnedMeshRenderer, Mesh) with no third-party reflection, so adding a tool is usually one self-contained `[InitializeOnLoad]` static class.

## What's in the box

- **Weight Sanity Check.** Detects the most common Blender-weight-transfer mistake on humanoid avatars: vertices on one side of the avatar (a left-leg garter, say) picking up non-trivial weight from a bone on the other side (right thigh). When you spread the legs the bad vertices stretch or follow the wrong limb. The tool walks every SkinnedMeshRenderer under a Humanoid Animator, classifies each vertex's bind-pose position as Left / Right / Center using the avatar's actual bone geometry, and classifies each weighted bone by Humanoid ancestry or spatial position. Per-issue Ping + Frame buttons, a *Wobble* button that moves the offending bone without moving the Scene camera, optional Scene-view gizmos, verbose logging, and a *Dump weights for selection* debug button are included. PhysBone clipping is intentionally split into its own one-mesh tool so normal weight scans stay fast. *(Tools > WhyKnot > wk-vrc-qol > Weight Sanity Check, or right-click an avatar in the hierarchy > WhyKnot > wk-vrc-qol > Check weights)*
- **PhysBone Clipping Risks.** A separate, heavier scan for likely PhysBone mesh clipping. It checks one SkinnedMeshRenderer at a time, estimates motion from actual PhysBone settings, and compares that motion envelope to the target mesh plus any comparison meshes you add. The current window is review-only: rows include Frame, Reveal, and Wobble controls for manual inspection without editing PhysBone or mesh settings. *(Tools > WhyKnot > wk-vrc-qol > PhysBone Clipping Risks, or right-click a mesh/avatar in the hierarchy > WhyKnot > wk-vrc-qol > Check PhysBone clipping)*
- **Auto Mesh Fixes.** UI-first stored setups for nondestructive clothing fit fixes. Pick a clothing mesh and body mesh, then Avatar QoL stores the intent on a small editor-only component. Preview hides the current avatar and shows a temporary processed copy with generated blendshapes enabled; play mode and upload generate the same temporary mesh clones without editing imported FBX/model assets. First version supports clothing-tighten and body-hide-by-blendshape, not true geometry deletion. *(Tools > WhyKnot > wk-vrc-qol > Auto Mesh Fixes > Open, or right-click an avatar/mesh in the hierarchy > WhyKnot > wk-vrc-qol > Auto Mesh Fixes)*
- **Weight Transfer (early).** Copies skin weights from one `SkinnedMeshRenderer` to another with projected/nearest source-surface matching, barycentric weight blending, source-to-target bone mapping, and topology inpainting for rejected vertices. Apply writes to a generated mesh clone under `Assets/AvatarQol Generated/`; imported FBX/model sub-assets are never edited in place. *(Tools > WhyKnot > wk-vrc-qol > Weight Transfer, or right-click a renderer in the hierarchy > WhyKnot > wk-vrc-qol > Weight Transfer)*
- **Mask Painter.** Paints mask textures directly on the avatar in Scene view, with UV-preview diagnostics, channel-aware painting, symmetry support, and GPU regression coverage for the brush and live overlay paths. The preview is drawn as an editor annotation so painted pixels stay visible on concave and two-sided mesh sections. *(Tools > WhyKnot > wk-vrc-qol > Paint Mask, or right-click an avatar/mesh in the hierarchy > WhyKnot > wk-vrc-qol > Paint mask)*
- **Mesh Sculpt (early).** Clones a selected SkinnedMeshRenderer mesh to `Assets/AvatarQol Generated/`, assigns the clone back to the renderer, and edits that clone in Scene view. First pass supports vertex selection, selected-vertex move, grab/smooth/inflate brushes, and triangle/quad face fill between existing selected vertices. Imported FBX/model sub-assets are never edited in place. *(Tools > WhyKnot > wk-vrc-qol > Mesh Sculpt, or right-click a renderer in the hierarchy > WhyKnot > wk-vrc-qol > Mesh Sculpt)*
- **Bone Merger.** Collapses duplicate or stray rig bones onto the bone that should own their skin weights. Meshes imported from FBX/model files are cloned to generated `.mesh` assets before writes, child transforms can be re-parented onto the kept bone, and the whole operation is one Undo step. *(Tools > WhyKnot > wk-vrc-qol > Bone Merger, or right-click an avatar in the hierarchy > WhyKnot > wk-vrc-qol > Merge bones)*
- **PhysBone Preset (early).** A library of smart presets that set up VRChat PhysBones on a selection of bones. Built-in presets: Tail, Ears, Hair, Dress/Skirt, plus a Generic fallback. Each preset reads a structural analysis of the selection (chain count, bone count per chain, average bone size, dominant orientation in avatar local space, nearest Humanoid ancestor) and adapts its parameters: gravity scales with verticality, spring loosens on longer chains, hair adds a head sphere collider sized to the avatar, dresses auto-add capsule colliders along the legs. Auto-suggests the best-fit preset on selection; preview-then-apply UI shows exactly what will be created before any mutation. *(Tools > WhyKnot > wk-vrc-qol > Apply PhysBone Preset, or right-click bones in the hierarchy > WhyKnot > wk-vrc-qol > Apply PhysBone preset)*
- **Logs + Hot Reload Status.** Per-package session logs live under `%LocalAppData%/WhyKnot/Logs/dev.whyknot.wk-vrc-qol/`, and the hot-reload watcher has its own status window. *(Window > WhyKnot > Avatar QoL > Logs / Hot Reload Status)*

Detailed walkthroughs of every tool live in the [Tools Overview](https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview) wiki page.

## Installation

### VCC (recommended)

Add the WhyKnot VPM listing to the [VRChat Creator Companion](https://creators.vrchat.com/), then this package shows up under **Manage Project -> Add Package**.

1. Click <https://vpm.whyknot.dev/>. The page redirects to a `vcc://` handler URL and VCC opens with the listing pre-filled. Click **I Understand, Add Repository**.
2. If that doesn't work, in VCC go to **Settings -> Packages -> Add Repository**, paste `https://vpm.whyknot.dev/index.json`, click **I Understand, Add Repository**.
3. Open any project, click **Manage Project**, find **Avatar QoL** in the package list, hit **Add**.

Unity compiles the package into a dedicated `dev.whyknot.wk-vrc-qol.Editor` assembly (`Editor/` only, nothing leaks into runtime builds). Hard-depends on `com.vrchat.avatars` and `com.vrchat.base` (>= 3.10.0); VCC will refuse to install without the VRChat SDK present.

### Manual install

For Unity projects not managed by VCC: download `dev.whyknot.wk-vrc-qol-X.Y.Z.zip` from [the latest release](https://github.com/RealWhyKnot/wk-vrc-qol/releases/latest), unzip into `Packages/dev.whyknot.wk-vrc-qol/` (so `Packages/dev.whyknot.wk-vrc-qol/package.json` exists), and Unity's Package Manager picks it up on next refresh.

Tested on Unity **2022.3** with VRChat SDK **3.10+**.

For per-clone development setup (symlink-for-development, etc.) see the [Installation](https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Installation) wiki page.

## Adding your own tool

A tool is a small `[InitializeOnLoad]` static class with one or more `[MenuItem]` entry points. The framework provides shared utilities (path formatting, Humanoid side mapping) but otherwise stays out of your way. The full developer guide lives at [Adding a Tool](https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Adding-a-Tool).

## Documentation

- [Wiki home](https://github.com/RealWhyKnot/wk-vrc-qol/wiki): long-form docs
- [Tools Overview](https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview): every shipping tool
- [Architecture](https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Architecture): framework design + heuristics
- [Adding a Tool](https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Adding-a-Tool): developer guide
- [Troubleshooting](https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Troubleshooting): common failure modes

## Heuristic, not exhaustive

Weight and PhysBone clipping checks are heuristics on top of bone geometry. They catch common failure modes (cross-side bleed and likely motion-envelope clipping), but they won't catch every issue and can produce false positives. Always:

1. Treat results as "look here" hints, not "fix this exactly."
2. Commit your project to version control before doing anything destructive based on a scan.
3. Tune the weight floor + center margin to your avatar's geometry.

## Contributing

Bug reports, feature requests, and pull requests are all welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the dev loop and PR conventions.

## License

Licensed under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE) for the full text.
