# Notes for certification

## App type

PackingProof is a full-trust WPF desktop app packaged as an x64 MSIX. It is not a UWP app.

## How to test

1. Launch the app and choose a workstation role. "Recording workstation" is the main camera/recording role.
2. Open Settings to review camera, storage and network options.
3. On a recording workstation, scan a barcode with a camera or barcode scanner, or use the start-recording button. The recording includes a short pre-record buffer from before the scan; this is expected.
4. Open the playback page to search recordings by tracking number.
5. The LAN web server is optional. Enabling LAN access may show a Windows UAC prompt because the app adds a URL ACL and firewall rule for the selected port.

## Capabilities

- `runFullTrust`: required for a packaged WPF desktop app.
- `webcam` / `microphone`: used only when the user starts camera recording.
- `internetClient`: optional online TTS, extension market downloads, and update metadata for the non-Store edition.
- `privateNetworkClientServer`: optional LAN playback, device discovery, phone/PC backup, and order integration.

## Updates

The Store edition does not run the portable edition's launcher or AppPatch updater. Updates are delivered by Microsoft Store. The app detects package identity and skips external update checks.

## Local data

Recordings, the SQLite database, logs and settings are stored locally in the package LocalState folder or in a user-selected storage location (including removable drives and NAS). PackingProof does not operate a cloud service for recordings.

## Online services

- Microsoft Edge TTS may send text that the user chooses to have spoken (order notes, buyer messages, product information) to Microsoft's speech service. Users can select Windows TTS or a local TTS option instead.
- The optional extension market reads a catalog and downloads signed extension packages from GitHub/Gitee.
- The non-Store edition checks GitHub/Gitee for update metadata. The Store edition uses Microsoft Store updates.

## Extensions

Extensions are optional user-installed scripts or external adapters. They are not executed inside the core recording process. The app shows permissions, verifies package signatures, and requires user confirmation before installation. No extension is required for the core recording, playback or storage features.

## Privacy

Privacy policy text is provided directly in Partner Center. The app has no account system, advertising, analytics or telemetry.
