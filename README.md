# Synthos Outfit Batch Uploader

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

## How to Open

- **Unity Menu Bar:** `Tools > Synthos > Outfit Batch Uploader` or `Window > Synthos > Outfit Batch Uploader`
- **Legacy Shortcut:** `Tools > Batch Uploader`

## Installation via VPM (VRChat Creator Companion / ALCOM)

Add the Synthos package repository:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then search for **Synthos Outfit Batch Uploader** and click **Install**.

## Credits & Acknowledgments

- **Upstream Project:** Forked from and based on [TheSilentD3ath/VRChat-outfit-batch-uploader](https://github.com/TheSilentD3ath/VRChat-outfit-batch-uploader) (MIT License).
- **Enhancements:** Extended by Synthos with multi-platform linking (Android/iOS), material overrides, domain reload survival, and VPM package automation.

## License

MIT License - Copyright (c) 2026 Synthos / TheSilentD3ath.
