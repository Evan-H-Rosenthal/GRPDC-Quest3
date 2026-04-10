# Quest 3 Handtracker

Unity project for Meta Quest 3 development.

This repository is intended to be opened directly in Unity and built for Android/Quest. The focus of this README is installation and environment setup so another developer can get the project running locally.

## Requirements

- Unity Hub
- Unity Editor `6000.2.13f1`
- Android Build Support for that Unity version
- Android SDK + NDK + OpenJDK installed through Unity Hub
- A Meta Quest 3
- Developer Mode enabled on the headset
- USB connection or other supported deployment workflow for installing Android builds

## Included In The Project

- `Assets/`
- `Packages/`
- `ProjectSettings/`
- Android OpenCV bridge sources under `Assets/Plugins/Android/src`
- Native OpenCV Android library under `Assets/Plugins/Android/libs/arm64-v8a`

Generated Unity folders such as `Library/`, `Temp/`, and local build artifacts are intentionally excluded from version control.

## Clone And Open

1. Clone the repository.
2. Open Unity Hub.
3. Add the cloned folder as an existing project.
4. Open the project with Unity `6000.2.13f1`.
5. Let Unity finish importing packages and generating the local `Library/` folder.

## Recommended Unity Modules

When installing Unity `6000.2.13f1`, make sure these modules are included:

- Android Build Support
- OpenJDK
- Android SDK & NDK Tools

## Package Setup

The project uses packages declared in `Packages/manifest.json`, including:

- Meta XR Core SDK
- Meta XR Interaction SDK
- Meta XR MR Utility Kit
- OpenXR
- XR Management
- Universal Render Pipeline
- Input System

Unity should restore these automatically when the project opens.

## Android / Quest Setup

1. In Unity, switch the active platform to `Android` if it is not already selected.
2. Confirm the headset is in Developer Mode.
3. Connect the Quest 3 to the development machine.
4. Accept any USB debugging prompt inside the headset.
5. Build and deploy from Unity.

Current Android project settings in this repo:

- Product name: `Quest 3 Handtracker`
- Application ID: `com.EvanRosenthal.CapstoneHandTracker`
- Minimum SDK: `32`
- Target architecture: `ARM64`
- Scripting backend: `IL2CPP`

## Camera Permission

This project requests the headset camera permission at runtime:

- `horizonos.permission.HEADSET_CAMERA`

If the permission is denied, passthrough camera features will not function correctly.

## First Launch Checklist

After opening the project, verify the following before building:

1. Package import completes without errors.
2. The main scene opens successfully.
3. Android platform is selected.
4. OpenXR and Meta XR features are enabled as expected for the project.
5. The Quest device is visible to Unity/ADB.

## Troubleshooting

### Unity opens but packages fail to import

- Reopen the project in the exact Unity version listed above.
- Verify internet/package access if Unity needs to restore packages.
- Delete the local `Library/` folder and reopen the project if imports are stuck.

### Android build fails

- Confirm Android Build Support, SDK, NDK, and OpenJDK are installed through Unity Hub.
- Verify Quest developer mode and USB debugging are enabled.
- Check that the Android platform is active in Build Settings.

### OpenCV / Android bridge issues

The project expects both of these to remain present:

- Java wrapper sources in `Assets/Plugins/Android/src/org/opencv/`
- Native Android library in `Assets/Plugins/Android/libs/arm64-v8a/libopencv_java4.so`

If either side is missing or version-mismatched, Android compilation or runtime marker tracking may fail.

## Repo Notes

- This repository should commit source assets and settings, not generated Unity output.
- Do not commit `Library/`, `Temp/`, `Logs/`, build APKs, or editor-local settings.
- If you create signing keys locally, keep them out of the repository.
