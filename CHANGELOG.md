# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-10-17

### Added

- **New Outfit Setup** (Express and Advanced) - Outfits that don't have a Blueprint ID yet get an **Express setup** button. It clears the PipelineManager's Blueprint ID so the SDK creates a new avatar, applies your default name, description, release status and content warning tags, captures a thumbnail (from a scene camera against a solid color, or a fixed image), builds and uploads, and writes the new Blueprint ID back into the outfit. **Advanced** opens the same flow with the name, description, tags, release status and thumbnail editable for that outfit first. If SPS or DPS components are found on the avatar, *Sexually Suggestive* is ticked automatically. New avatars are always created from the PC (Windows) build target and the PC avatar root. If an upload fails after the avatar was created, its Blueprint ID is kept so the next try doesn't create a second avatar. The defaults (`{outfit}` and `{avatar}` work in the name and description) are saved across sessions. Ported from [TheSilentD3ath/VRChat-outfit-batch-uploader](https://github.com/TheSilentD3ath/VRChat-outfit-batch-uploader).
- **Tabs** - The window is split into **Outfits**, **New Outfits** (with a count of outfits that still need setup) and **Defaults**.
- **Why each upload was skipped** - The end-of-queue summary shows the reason next to every skipped or failed upload, read from the SDK's own error, including each entry of a validation error list and errors wrapped in other exceptions. Each outfit is listed once: platforms that failed for the same reason share a line, others get a line each, and the header counts both, e.g. "3 uploads, 2 outfits". The Console logs a "Skipped '<outfit>' (<platform>) - reason: ..." line for every skip and puts the reason at the top of the full error, and validation skips show the reason in the status bar.
- **Update Descriptions** (next to Base Version) - Writes the Base Version into the description of every included outfit without building or uploading. Its dropdown has **Replace full description...**, which sets the whole description for all of them (`{version}` is replaced by the Base Version). Failures are logged with their reason and listed in the status bar.
- **Avatar Compressor size adjustments in the summary** - When Avatar Compressor's "Keep under the mobile size limit" makes textures smaller (or the avatar is still over the limit), it hands its report to the uploader instead of showing a popup, so the batch never stops for it. The end-of-queue summary lists them under **Adjusted to fit**, one line per outfit and platform.

### Changed

- **Blendshapes are backed up and restored per avatar root** like materials, so one outfit's offsets no longer carry over into the next, also in Same Project Mode.
- **Cancel waits for the current build to stop**, then restores the base materials and blendshapes. A failed build target switch now stops the batch instead of uploading for the wrong platform.
- **Only the SDK's own validation and build-blocked errors are skipped automatically.** Other errors stop the batch unless "Proceed on error" is on.
- **Base Version only replaces the old version text** in the description, instead of rewriting it.
- The upload mode and the Android/iOS link setting are remembered between sessions.
- The legacy `Assets/VRC_Batch_Uploader` folder is migrated before cleanup is offered, and moved to the trash instead of deleted.
- Outfits that are missing from the scene are reported as skipped.

### Removed

- The option to accept the SDK's build auto-fixes without review.

### Fixed

- **Version updates on descriptions saved by VRChat** - VRChat stores dots as a look-alike character (U+2024), so "v3.19" could become "v3.19.19". Both are now recognized.
- **Materials embedded in FBX files and scene-only materials** are now restored correctly. Original materials are recorded by their global object ID; backups from older versions can still be read.

## [1.0.3] - 2026-10-05

### Added

- **Error sound** - Plays an alert when an upload fails.
- **Proceed on error** - If an outfit fails, logs a warning and continues with the rest of the queue instead of stopping the batch.

## [1.0.2] - 2026-10-05

### Removed

- The legacy **Tools > Batch Uploader** menu shortcut.

## [1.0.1] - 2026-10-05

### Changed

- Menu items and the display name are now **Batch Uploader**.

## [1.0.0] - 2026-10-05 - Synthos Edition

First release as a VPM package, based on [TheSilentD3ath/VRChat-outfit-batch-uploader](https://github.com/TheSilentD3ath/VRChat-outfit-batch-uploader) v1.0.

### Added

- Uploads every outfit for PC, Android and iOS in one run, grouped by platform so the build target only switches once per platform.
- Per-slot material overrides for each platform, and per-outfit blendshape offsets, applied before each build and restored afterwards.
