# DAC Guard TEST 0.3

This branch, `test/dac-ui-lock-v03`, preserves the isolated `1.0.0-beta.7-dac0.3` acceptance-build snapshot. It derives from contribution commit `c05ce188796bf792c87c238d7fa73f5fb53b3b5e` of [Windows Lossless Switcher](https://github.com/jordanmgibson/WindowsLosslessSwitcher), originally authored by jordanmgibson. The original project name, history, GPL-3.0-or-later license, authorship, third-party notices, and upstream repository links are retained. This branch is not an official release and does not update the existing upstream contribution PR with these DAC/UI changes.

## What changed

- DAC Guard restricts protected audio work to an eligible external DAC and verifies the default playback routes before operating. It blocks missing, ambiguous, or unverified hardware instead of probing or switching a display speaker.
- The dac0.2 responsiveness work moves physical-device inventory, UI queries, transport availability, and spectrum creation to background workers. UI projections use cached state, duplicate polls coalesce, and shutdown drains workers before disposing their dependencies.
- dac0.3 brings the compact Nocturne layout closer to the original author's interface, keeps Inter for Latin text, and bundles Noto Sans CJK TC Regular/Medium for Traditional Chinese. Text uses Ideal formatting, Grayscale rendering, layout rounding, and a PerMonitorV2 manifest.
- **Pause DAC Lock** and **Resume DAC Lock** are available in the main window, tray flyout, and settings. English is the initial language; **Settings → Switching → Language** can select **繁體中文** without restarting either app.

## Supported hardware and routing

The current rule supports **TOPPING DX1 II**, identified by a verified physical USB ancestor with `VID_152A&PID_8750` and an eligible active render-endpoint form factor. This vendor/product identifier describes the supported model, not a personal device serial number. A friendly name, a USB bus, or a Speakers label alone does not establish eligibility. This branch does not claim support for all TOPPING products or all external DACs.

While DAC lock is enabled, the tool checks that the supported DAC is connected and that the Windows **Console** and **Multimedia** default playback roles point to it. It attempts to route those two roles to the verified DAC when necessary and confirms the result before allowing protected operations. Missing hardware, multiple ambiguous candidates, stale identity, or failed/unconfirmed routing blocks those operations. The **Communications** default is not changed. Default playback devices are not automatically restored on exit.

This remains shared-mode audio; DAC lock does not provide exclusive mode or prove bit-perfect physical output. Protective mute affects the whole target output device, including other applications, while preserving a pre-existing user mute.

## Use another speaker

1. Pause the music when taking a break, then select **Pause DAC Lock**.
2. Wait for the paused state or follow any displayed cleanup guidance.
3. Select another speaker yourself in Windows Sound settings. The tool does not choose a replacement speaker for you.
4. When ready to use the supported DAC again, select **Resume DAC Lock**. The tool revalidates hardware and default playback roles. Music that was already paused is not automatically started.

The saved `dacLockPaused` setting is applied before startup routing and is also respected by restart helpers. A restart does not implicitly re-enable the lock. While paused, new automatic routing, format probing/switching, protective mute acquisition, Play, and navigation are blocked. Ordinary healthy playback is not forcibly paused merely because the lock is toggled.

If the tool owns an in-progress protected operation, pause cancels old work and settles it on a worker. It may need to confirm a safe player pause before releasing only its own mute on the same DAC. An uncertain command result or unconfirmed cleanup can leave the lock paused with protective mute retained and explicit guidance. Do not treat a paused policy as proof that every in-flight remote command has finished. Failed resume remains paused and requires an explicit retry. Tool-only restart is available when safe; combined player/tool restart is disabled while the lock is paused.

## Isolation and defaults

The assembly/executable identity is `WindowsLosslessSwitcher.DacGuardTest`; runtime data is confined to `%APPDATA%\WindowsLosslessSwitcher.DacGuardTest`. This is the same DAC-test profile used by dac0.1 and dac0.2. Installed-app settings and legacy data are not migrated into it. Use dac0.3 when relying on the new saved pause policy, because older builds do not implement it.

A fresh profile uses English, 24-bit default depth with track bit-depth following off, and **Restart track after sample-rate changes (experimental)** on. Existing DAC-test preferences are otherwise preserved. The test profile enforces Follow Default mode, clears device pinning, and disables automatic Apple Music restart, startup registration, installer lifecycle, and app updates. The original project's repository/update metadata is retained, but this build does not perform updates.

All versions retain the shared single-instance mutex. Exit other Switcher versions before starting this one; the separate data folder does not permit simultaneous controllers. This branch does not overwrite an installed executable when built or run from its own output folder.

## Build and run

Use Windows and the .NET 8 SDK. From this repository's root:

```powershell
dotnet build .\WindowsLosslessSwitcher.sln -c Release
dotnet test .\WindowsLosslessSwitcher.sln -c Release
```

To create a self-contained x64 folder without changing an installed copy:

```powershell
dotnet publish .\src\WindowsLosslessSwitcher.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\dac-guard
```

After exiting other Switcher versions, run `WindowsLosslessSwitcher.DacGuardTest.exe` from the complete publish folder. Building and testing do not require starting the app or changing audio output. Preserve the complete output, [LICENSE](LICENSE), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and font licenses when redistributing. Font source revisions and checksums are recorded in `src/Assets/Fonts/NotoSansCJK-provenance.json`.

The inherited release workflows expect the normal application identity and do not package this isolated identity. Do not create release tags or use those release workflows for this branch; use the build/test/publish commands above. The current CI workflow runs for main-branch pushes and pull requests, so pushing this test branch alone does not establish a CI result.

## Validation and remaining limits

Local validation for the product source in this snapshot completed on October 31, 2026:

- **605 tests passed**, with zero failed or skipped. Coverage uses fake devices/transports, including paused routing polls, persistence/helper startup, cleanup uncertainty, resume, and UI command behavior.
- **80 offline UI images** passed with zero binding errors, including English/Traditional Chinese switching and lock states in the main window, tray, and settings.
- Product GlyphRun checks used the actual bundled Inter/Noto resources at **96, 120, 144, and 192 DPI**.

These results are local test evidence, not GitHub CI results, actual DAC routing/listening acceptance, or live multi-monitor placement tests. This snapshot's build/package verification did not start the real tool, control Apple Music, or change host playback defaults. No new end-to-end hardware acceptance is claimed here.

The first manually selected track may still sound before protective mute is acquired; about two seconds was reported in an earlier isolated build. That issue is documented and deferred, not proven unavoidable. This version also does not repair a speaker/driver already left in a noisy state. Remote commands already accepted by Apple Music cannot be withdrawn, and shared-mode output, driver behavior, player queue behavior, sleep/wake, and ARM64 compatibility still require appropriate real-device verification.
