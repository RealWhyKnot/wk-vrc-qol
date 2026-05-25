# Troubleshooting

Common failure modes and false-positive scenarios. If your problem isn't here, [file a bug](https://github.com/RealWhyKnot/vrc-avatar-qol/issues/new?template=bug_report.yml).

## Menu items don't appear

- **First-time install.** Focus Unity once after dropping in `Editor/`. Unity needs to compile the scripts before the menu items show up.
- **Tool entry greyed out under `GameObject/WhyKnot/vrc-avatar-qol`.** The validator skips entries when the selection isn't appropriate. For Weight Sanity Check this means: select a GameObject with a Humanoid Animator (or one in its descendants).

## Weight Sanity Check: "Animator is not Humanoid"

The symmetry check needs Humanoid bone bindings (LeftUpperLeg, RightUpperLeg, Hips). Generic / non-Humanoid rigs aren't supported. Set the rig type to Humanoid in the model importer's *Rig* tab and re-bind the bones.

## Weight Sanity Check: too many false positives

A few common causes:

- **Mesh that bridges sides by design.** Capes, dresses, tails, scarves often have legitimate weights from both Left and Right bones. Add the renderer to the *Exclude renderers* list at the top of the window -- those won't be scanned.
- **Spine bleed flooding the list.** With *Center margin* at the default 0, spine/torso vertices very close to the centerline can swing across as bind-pose noise. Raise *Center margin* toward 0.005 m until the noise drops, or tick *Scan centre-band vertices* off so any vertices you do reclassify as Center stop contributing.
- **Weight floor too low.** With the default 0, tiny rounding/smoothing weights surface alongside real bleed. Raise *Weight floor* (try 0.001..0.02) until only the weights you'd actually call cross-side bleed remain.
- **Custom bones outside the Humanoid hierarchy.** A bone with no Humanoid ancestor reports as `Unknown` and is skipped by the check. If your custom bones live under a chest/parent that ISN'T tagged Humanoid, the side won't propagate. Re-parent the custom chain under the appropriate Humanoid bone (e.g. `LeftShoulder` for a left-arm prop chain).

## Weight Sanity Check: missing real issues

- **Weight floor too high.** Lower it toward 0 to see weights you'd consider negligible. Real cross-side bleed in the 0.0001..0.001 band still visibly stretches the mesh under animation.
- **Mesh is non-readable.** Importers can mark a mesh as not-readable for runtime memory savings. The tool can still open `Mesh.vertices` / `GetAllBoneWeights()` in the editor, but if you've pre-baked the mesh to a `MeshCollider` or shipped it as an asset, double-check *Read/Write Enabled* in the importer.
- **Vertex is in the center band.** A vertex in the centerline margin isn't classified as Left or Right and won't be flagged regardless of how it's weighted. With *Center margin* at the default 0 this stops being an issue; if you've raised it and a vertex you expect flagged is suspiciously central, lower *Center margin* back toward 0.

## Scene-view gizmos don't appear

- Toggle *Show gizmos in Scene view* in the window.
- Make sure Gizmos are enabled in the Scene view itself (top-right toolbar).
- The gizmos are drawn via `SceneView.duringSceneGui`, which only runs while a Scene view is open and visible.

## "Frame" button doesn't move the camera

The *Frame* button calls `SceneView.lastActiveSceneView.LookAt(...)`. If no Scene view has been focused recently, `lastActiveSceneView` may be null. Click into the Scene view once and try again.
