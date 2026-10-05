# Synthos Batch Uploader

A comprehensive Unity Editor workflow automation tool for VRChat avatar creators managing multiple outfits across multiple platforms (PC Windows, Android Quest, and iOS).

Automates tag switching, blueprint ID assignment, per-outfit blendshape offsets, per-slot material overrides, and seamless batch uploads without manual intervention.

## Features

- **Multi-Platform Batch Synchronization:** Supports **Single Avatar** and **Same Project** modes, automatically linking and synchronizing PC, Android, and iOS avatar roots.
- **Material Overrides Engine:** Configures per-slot material overrides for both PC and Android/iOS. Automatically swaps materials during upload and safely restores base materials afterwards.
- **Per-Outfit Blendshapes:** Captures and applies custom blendshape offsets per outfit on the avatar's SkinnedMeshRenderer (e.g. heels compensation, body shaping) and restores base values upon completion.
- **Cross-Platform Batching:** Batches uploads by target platform to minimize slow Unity platform switching, and survives domain reloads and recompilations via persistent session states.
- **Blueprint ID Management:** Seamlessly manages PipelineManager blueprint IDs for all outfits and platforms.
- **Audio & Visual Feedback:** Custom completion sound and platform-coded progress indicators.
- **Automated Legacy Migration:** Detects existing project settings and preserves all previously configured outfit data, blueprint IDs, blendshapes, and material overrides from `ProjectSettings/VRC_Batch_Uploader/` and `ProjectSettings/ShiroTools/`.

## Installation via VPM (VRChat Creator Companion / ALCOM)

Add the Synthos package repository:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then search for **Synthos Batch Uploader** in the package list and click **Install**.

## How to Open

- **Unity Menu Bar:** `Tools > Synthos > Batch Uploader` or `Window > Synthos > Batch Uploader`

## How to Use

### 1. Scene Setup
1. Under your avatar root, ensure all outfit GameObjects are placed as direct children under a single container GameObject (named **`Outfits`** by default).
2. If using **Same Project** mode for cross-platform uploads, have your PC, Android, and/or iOS avatar roots in the same scene.

### 2. Tool Configuration
1. **Upload Mode:**
   - **Single Avatar:** For uploading an avatar to the active build target.
   - **Same Project:** Synchronizes separate PC, Android, and iOS avatar root hierarchies within one scene.
2. **Avatar Roots:** Assign your avatar root GameObject(s). If only one avatar is present, it will be auto-detected.
3. **Avatar Skin:** Assign the SkinnedMeshRenderer containing body or outfit blendshapes (auto-detected).
4. **Outfits Parent:** Enter the name of your outfits parent container (default: `Outfits`).

### 3. Configuring Outfits
1. **Blueprint ID:** For each detected outfit, paste its unique VRChat Blueprint ID (`avtr_...`).
2. **Target Platforms:** Check **Win**, **And**, and/or **iOS** to define which platforms each outfit should build for.
3. **Blendshape Overrides:**
   - Expand the outfit's **BlendShapes** foldout.
   - Adjust your avatar's blendshapes in the scene (e.g. high-heel foot angle, clothing shrink shapes).
   - Click **"Capture current skin values"** to lock in those offsets for that specific outfit.
4. **Material Overrides:**
   - Expand **Material Overrides** to specify custom materials per mesh slot.
   - Assign separate PC and Android override materials.
   - The tool swaps them in before building and reverts to your original base materials after the upload finishes.

### 4. Uploading
- **Select Button:** Activates a single outfit in the scene (sets tags to `Untagged` / `EditorOnly`, updates the PipelineManager ID, and applies blendshapes) for inspection without building.
- **Upload Button:** Directly builds and uploads that specific outfit.
- **Batch Upload All:** Sequentially builds and uploads every outfit marked with **"Include in batch"**. Asks for SDK confirmation once at the start, handles platform switches with persistent queue recovery, and plays a completion chime when finished.

## Credits & Acknowledgments

- **Upstream Project:** Forked from and based on [TheSilentD3ath/VRChat-outfit-batch-uploader](https://github.com/TheSilentD3ath/VRChat-outfit-batch-uploader) (MIT License).
- **Enhancements:** Extended by Synthos.

## License

MIT License - Copyright (c) 2026 Synthos / TheSilentD3ath.
