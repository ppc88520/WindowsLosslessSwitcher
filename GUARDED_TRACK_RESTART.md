# Guarded track restart after a sample-rate change

Enable **Settings → Switching → Restart track after sample-rate changes (experimental)** to try this behavior. The default is off, including when loading an older settings file that does not contain `restartTrackAfterSampleRateChange`. Normal switching and recovery remain the default.

The experiment addresses a specific problem: Apple Music's timeline can advance while its audio stream recovers from a device-format change, so audible playback begins several seconds into a track. This option holds output muted until the same current track is confirmed paused at its beginning, then resumes it.

## Behavior

1. Resolve the current track's format and determine whether the selected device's actual sample rate must change. Same-rate tracks, including bit-depth-only changes, keep the existing switching behavior.
2. Verify the current media session and track, mute the target device, and confirm a pause before applying the new format. Wait for the device to report the selected format consistently.
3. Resume while muted to observe Apple Music's own audio session. If audio does not return, try one muted pause/play recovery. This path does not skip a track, bounce the device between formats, or restart Apple Music to recover a renderer.
4. Require both a reported track position of at least ten seconds and at least ten seconds since Playing was first observed during muted preparation. The second check prevents a stale timeline from the preceding track from allowing an early Previous request.
5. Pause again and send one guarded Previous request to the captured session. Confirm the same track is paused within 250 ms of zero, across multiple observations for 500 ms. If Apple Music accepts Previous but defers it while paused, allow one muted Play activation and pause again when zero is observed.
6. Recheck track identity, paused zero, endpoint mute, and the selected device format immediately before releasing the mute and playing. An accepted command without a confirmed reset is not success and cannot unmute playback at its old position.

Preparation commonly takes around 15–25 seconds on the reported setup. Metadata/network latency, renderer recovery, and rejected playback commands can make it longer. Remote commands and reads have bounded waits; the coordinator stops rather than retrying indefinitely.

If playback was already paused by the user, it remains paused. A superseding track cancels old work. A locally observed navigation command or a changed captured session cannot redirect an in-flight restart to another session.

## Media-session lifetime

Session refresh and detach operations are serialized. Each subscription captures a session epoch; stale callbacks and stale asynchronous metadata/artwork reads cannot update the current item. Native COM identity distinguishes different WinRT wrappers of one session from genuinely replaced sessions; an application ID alone is insufficient.

Fresh checks re-enumerate sessions so an idle, replaced session can be attached again. If the Previous preflight has not dispatched any navigation, it may refresh once and verify the same paused track again. After navigation has been dispatched, the captured session is fixed and cannot be replaced for a retry.

## Limitations and recovery

- GSMTC reports the current item asynchronously. There is no pre-track queue hook here, so pausing at the exact first audio sample cannot be guaranteed. Cold format lookup also occurs before the new pause; a brief initial fragment before the app obtains mute control remains possible.
- Apple Music on the reported setup does not enable external absolute seeking. Previous semantics belong to the player; ten seconds is a conservative experimental threshold, not an Apple guarantee.
- If Previous exposes the known preceding track, one Next correction is allowed only with verified safe queue conditions. Unknown items, shuffle/queue uncertainty, session changes, or unresolved commands stop automatic handling. Remote commands that the player already accepted cannot be withdrawn, so transient navigation cannot be ruled out.
- The mute applies to the entire target output device, including other applications. A pre-existing user mute is preserved.
- Paused/Inactive does not prove that WASAPI released an initialized audio client. Some players or devices may retain an invalidated dormant client after a paused format change. The limited muted recovery can fail; this requires device testing.
- Bit-depth-only changes retain normal behavior and can still interrupt playback. Disabling per-track bit-depth switching selects the configured default bit depth; changing that setting can therefore cause a one-time bit-depth change even between same-rate tracks.

When the UI reports **manual recovery required**, pause Apple Music first, exit the switcher from the system tray, select the intended track, and reopen the switcher. Exiting releases only the mute owned by the switcher. The strict failure path deliberately does not run the normal skip/restart recovery ladder.

## Validation

Unit tests use fake playback sessions and endpoints; they do not establish compatibility with every DAC or Apple Music build. They cover default-off behavior, legacy settings compatibility, session identity, bounded commands, stale state propagation, strict zero confirmation, cancellation, and preserving mute on uncertain results.

The independent prototype was reported to improve playback on Windows x64 build 10.0.26200.0, Apple Music 1.1540.23042.0, and TOPPING DX1 II. A local trace confirmed a 44.1 → 48 kHz transition paused before apply, restored muted audio, confirmed the same item at zero, and resumed. That is limited prototype evidence; the upstream port and the following matrix still need broader hardware validation.

| Manual test | Expected result |
| --- | --- |
| Option off, 44.1 ↔ 96 kHz | Existing live switching and recovery; no new automatic Previous |
| Option on, 44.1 ↔ 96 kHz | Pause before apply; correct target format; muted preparation; intended item starts from its beginning |
| Same-rate tracks, with and without the option | No additional pause/preparation/Previous from this feature |
| Pause for 15–30 minutes, then manually select a different-rate track | Session refresh works without restarting the switcher; the intended item restarts safely |
| Use another media app while Apple Music is idle, then resume | Only the intended Apple Music session receives commands; no duplicate subscription effects |
| Change tracks or pause during preparation | Old work cancels; no automatic replay of the old item |
| Close/reopen Apple Music or disconnect the device during preparation | Bounded failure or cancellation; no navigation retry on an unverified replacement session |

When reporting results, include the switch direction, whether audio leaked during preparation, whether the intended track remained selected, and whether output began at its start. Review diagnostics for personal information before sharing them.
