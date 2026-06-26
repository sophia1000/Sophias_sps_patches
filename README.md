# Sophia's SPS Patcher

Editor-only VRChat avatar SPS patching tools.

## Setup

1. Add this package to a Unity 2022.3 VRChat avatar project through VCC.
2. Open `SPS Patcher > Prefab Installer`.
3. Select an avatar, or enable `Install To All Avatars`.
4. Click one of the prefab buttons.

The installer removes any existing SPS patcher prefab from the avatar before adding the selected prefab.

## Upload Safety

The avatar component implements `IEditorOnly`, and the build hook runs before the SDK strips editor-only components. The tool is intended to patch the avatar during build and not upload runtime scripts.

## Prefabs

Place installer prefabs in the package `Prefabs` folder. The installer window finds them automatically when opened.
