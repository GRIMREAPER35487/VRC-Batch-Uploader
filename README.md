# Synthos Batch Uploader

> [!WARNING]
> **Safety & Backup Advisory:** While the batch uploader is built to be non-destructive by staging material and blendshape overrides in memory and reverting them post-upload, batch operations involve automated platform switching, scene tag updates, and VRChat SDK build calls. Always ensure your avatar project and scene are **backed up or committed to version control (Git)** before running large batch uploads.

A comprehensive Unity Editor workflow automation suite for VRChat avatar creators managing multiple outfits across multiple platforms (PC Windows, Android Quest, and iOS). 

Automates tag switching, blueprint ID assignment, per-outfit blendshape offsets, per-slot material overrides, and seamless cross-platform batch uploads without manual intervention.

> [!TIP]
> **Beyond Outfits — Thinking in "Avatar Configurations":** While designed and named around outfits, each entry is fundamentally a modular **Avatar Configuration**. Because each entry independently controls GameObject hierarchy branches, a unique Blueprint ID, custom body blendshape offsets, and per-slot material overrides, you can use it for far more than clothing:
> - **Hairstyle & Gear Loadouts:** Switch different hairstyles, hats, armor, weapons, or accessory combinations.
> - **Body Proportion Presets:** Maintain different body variants (chibi, tall, petite, athletic) via captured blendshapes.
> - **Colorway & Art Style Themes:** Swap full material palettes (dark/light themes, cel-shaded vs. realistic) without duplicating the base model.
> - **Tiered Editions:** Build Public vs. Private avatar releases with different accessories enabled or stripped.
>
> **Stop Maintaining Duplicate Projects or Desynced Avatar Versions:**
> Traditionally, managing multiple avatar editions meant duplicating your avatar across multiple scenes or maintaining entirely separate, disk-heavy Unity projects. Whenever you tweaked a PhysBone, fixed a weight paint issue, improved a facial gesture, or updated a base texture, you had to painstakingly repeat that manual update across every single version.
>
> With Synthos Batch Uploader, you maintain **one single base avatar in one project**. Any improvement you make to the base body, bones, or animations immediately benefits every configuration. You no longer have to manually sync settings between different avatar versions—just click **Batch Upload All**, and the tool automatically cycles through each configuration, applies its unique blendshapes, swaps materials, assigns its Blueprint ID, and uploads it across PC, Android, and iOS in one automated run.

## Features

- **Multi-Platform Batch Synchronization:** Supports both **Single Avatar** mode (for single-model workflows) and **Same Project** mode (synchronizing separate PC, Android, and iOS avatar roots within the same scene). Automatically links and synchronizes Android & iOS target configurations to avoid redundant setup.
- **Smart Material Override Engine:** Configures granular per-slot material overrides for PC, Android, and iOS. Allows creators to easily swap mobile-optimized shaders or quest fallback materials on clothing slots. Materials are staged dynamically before build and safely restored to base scene materials upon completion or cancellation.
- **Per-Outfit Blendshape Offsets:** Captures custom blendshape weights per outfit on the avatar's body/skin `SkinnedMeshRenderer` (e.g., high-heel foot angles, clothing shrink shapes, body adjustments). Automatically applies blendshapes when an outfit is selected and restores base values post-build.
- **Automated Tag & Build Leak Isolation:** Automatically switches active outfit GameObjects to `Untagged` while setting all inactive outfits to `EditorOnly`. This guarantees that inactive outfits and their textures/meshes are never bundled into the uploaded avatar file, preventing download size and VRAM bloat.
- **Cross-Platform Queue Batching:** Optimizes upload sequences by platform (grouping all Windows builds, then all Android builds, then all iOS builds) to minimize time-consuming Unity platform switches.
- **Domain Reload & Crash Recovery:** Uses persistent editor session storage to seamlessly survive Unity domain reloads, script recompilations, and platform switches mid-queue, resuming upload execution automatically once the VRChat SDK re-initializes.
- **Seamless Blueprint ID Management:** Stores and applies unique VRChat Blueprint IDs (`avtr_...`) for every outfit and platform, automatically updating the avatar's `PipelineManager` component before each build.
- **Interactive Outfit Selector & Inspector:** Features an in-editor **Select** button for every outfit, allowing creators to preview, pose, and inspect outfit blendshapes and materials directly in the Unity scene view with full Undo support.
- **Visual Progress & Audio Feedback:** Features color-coded platform indicators, detailed progress status bars, and an optional audio chime on batch completion.
- **Automated Legacy Migration:** Seamlessly detects and imports existing outfit data, blendshapes, material overrides, and blueprint IDs from previous tool configurations (`ProjectSettings/VRC_Batch_Uploader/` and `ProjectSettings/ShiroTools/`) without losing project data.

## How It Works

- **Scene Hierarchy Detection:** Locates your avatar root and identifies the outfits container (`Outfits` by default). Each direct child GameObject represents an individual outfit.
- **Non-Destructive State Staging:** When an outfit is activated or uploaded, base material assignments and original blendshape snapshots are safely cached in memory. The active outfit is tagged `Untagged` and enabled, while all other outfits are tagged `EditorOnly` and disabled.
- **SDK Builder API Hooking:** Interfaces directly with the official VRChat SDK builder (`VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>()`) to trigger automated avatar builds and uploads asynchronously.
- **Platform Grouping & Queue Recovery:** The batch queue organizes uploads by target platform to prevent unnecessary platform switches. When a platform switch occurs (triggering a Unity domain reload), session data persists across reloads and resumes the queue once the SDK builder is ready.
- **Automatic Post-Upload Reversion:** Once uploads finish or if a build is canceled, base materials and original blendshape snapshots are cleanly restored, and the avatar can optionally reset to its default outfit.

## How to Use

### 1. Scene Setup
1. Under your avatar root, ensure all outfit or configuration GameObjects are placed as direct children under a single container GameObject (named **`Outfits`** by default). Each child can represent an outfit, hairstyle variant, accessory loadout, or full avatar configuration.
2. If using **Same Project** mode for cross-platform uploads, have your PC, Android, and/or iOS avatar roots in the same scene.

### 2. Opening the Tool
- In the Unity menu bar, navigate to **Window** → **Synthos** → **Batch Uploader** or **Tools** → **Synthos** → **Batch Uploader**.

### 3. Tool Configuration
1. **Upload Mode:**
   - **Single Avatar:** For uploading an avatar to the active build target.
   - **Same Project:** Synchronizes separate PC, Android, and iOS avatar root hierarchies within one scene.
2. **Avatar Roots:** Assign your avatar root GameObject(s). If only one avatar is present in the scene, it will be auto-detected.
3. **Avatar Skin:** Assign the `SkinnedMeshRenderer` containing body or outfit blendshapes (auto-detected).
4. **Outfits Parent:** Enter the name of your outfits parent container (default: `Outfits`).

### 4. Configuring Outfits
1. **Blueprint ID:** For each detected outfit, paste its unique VRChat Blueprint ID (`avtr_...`).
2. **Target Platforms:** Check **Win**, **And**, and/or **iOS** to define which platforms each outfit should build for.
3. **Blendshape Overrides:**
   - Expand the outfit's **BlendShapes** foldout.
   - Adjust your avatar's blendshapes in the scene (e.g., high-heel foot angle, clothing shrink shapes).
   - Click **"Capture current skin values"** to lock in those offsets for that specific outfit.
4. **Material Overrides:**
   - Expand **Material Overrides** to specify custom materials per mesh slot.
   - Assign separate PC and Android/iOS override materials.
   - The tool swaps them in before building and reverts to your original base materials after the upload finishes.

### 5. Inspecting & Uploading
- **Select Button:** Activates a single outfit in the scene (sets tags to `Untagged` / `EditorOnly`, updates the PipelineManager ID, and applies blendshapes and material overrides) for inspection without building.
- **Upload Button:** Directly builds and uploads that specific outfit to the active platform.
- **Batch Upload All:** Sequentially builds and uploads every outfit marked with **"Include in batch"**. Asks for SDK confirmation once at the start, handles platform switches with persistent queue recovery, and plays a completion chime when finished.

### 6. Preferences & Settings
- **Reset to First Outfit:** Automatically resets the avatar in your scene to the first outfit after a batch upload finishes.
- **Sound Notification:** Toggles the audio chime played when a batch upload completes.
- **Link Android & iOS:** Keeps Android and iOS platform toggles synchronized so mobile configurations mirror each other.

## Requirements

- **Unity 2022.3** (VRChat avatar development environment)
- **VRChat Avatars SDK (VRCSDK3-AVATAR >= 3.5.0)**
- **Android / iOS Build Support:** Required in your Unity installation if compiling mobile avatar variants.

## Installation via VPM (VRChat Creator Companion / ALCOM)

Add the Synthos package repository:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then search for **Synthos Batch Uploader** in the package list and click **Install**.

## Credits & Acknowledgments

- **Upstream Project:** Forked from and based on [TheSilentD3ath/VRChat-outfit-batch-uploader](https://github.com/TheSilentD3ath/VRChat-outfit-batch-uploader) (MIT License).
- **Enhancements:** Extended by Synthos.

## License

MIT License - Copyright (c) 2026 Synthos / TheSilentD3ath.
