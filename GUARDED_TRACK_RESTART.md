# Guarded track restart after a sample-rate change

This document describes a proposed contribution to [jordanmgibson's WindowsLosslessSwitcher](https://github.com/jordanmgibson/WindowsLosslessSwitcher). The original project, authorship, Git history, GPL license, branding, and upstream update target are retained. The contributor's fork is used to submit the proposal for the original maintainer's review; it is not an official upstream release or a claim of ownership of the original work.

The isolated executable/profile used for prototype testing is not included in the upstream proposal. A build of this branch uses the original application's normal identity and data directory, so it should not be assumed to be isolated from an installed release.

Enable **Settings → Switching → Restart track after sample-rate changes (experimental)** to try this behavior. The default is off, including when loading an older settings file that does not contain `restartTrackAfterSampleRateChange`. Normal switching and recovery remain the default.

The experiment addresses a specific problem: Apple Music's timeline can advance while its audio stream recovers from a device-format change, so audible playback begins several seconds into a track. This option holds output muted until the same current track is confirmed paused at its beginning, then resumes it.

## Behavior

1. Resolve the current track's format and determine whether the selected device's actual sample rate must change. Same-rate tracks, including bit-depth-only changes, do not acquire the new pause/preparation/Previous sequence. When the experiment is enabled, its conservative recovery-failure policy still applies to these tracks.
2. If the user has already paused a different-rate track, leave its device format unchanged and wait for the user's Play. A separate playback-state notification wakes this deferred operation even when the track metadata is unchanged. Reverify the current session and track before continuing; a changed track or session cancels the old wait.
3. Verify the current media session and track, mute the target device, and confirm a pause before applying the new format. Wait for the device to report the selected format consistently.
4. Resume while muted to observe Apple Music's own audio session. If audio does not return, try one muted pause/play recovery. This path does not skip a track, bounce the device between formats, or restart Apple Music to recover a renderer.
5. Require both a reported track position of at least ten seconds and at least ten seconds since Playing was first observed during muted preparation. The second check prevents a stale timeline from the preceding track from allowing an early Previous request.
6. Pause again and send one guarded Previous request to the captured session. If Previous is temporarily unavailable, wait at most about two seconds while repeatedly verifying the same paused track and session. Never force a disabled command. Confirm the same track is paused within 250 ms of zero, across multiple observations for 500 ms. If Apple Music accepts Previous but defers it while paused, allow one muted Play activation and pause again when zero is observed.
7. Recheck track identity, paused zero, endpoint mute, and the selected device format immediately before releasing the mute and playing. An accepted command without a confirmed reset is not success and cannot unmute playback at its old position.

Allow roughly 15–25 seconds or longer for preparation. This is planning guidance, not a measured benchmark or a global timeout: the confirmed Playing interval alone must reach ten seconds, and metadata/network latency, renderer recovery, and rejected playback commands can add time. Remote commands and reads have bounded waits; the coordinator stops rather than retrying indefinitely.

The deferred path does not change the format or start playback until the user presses Play. A superseding track cancels old work. A locally observed navigation command or a changed captured session cannot redirect an in-flight restart to another session.

With the option enabled, exhausted audio recovery stops after bounded attempts on the intended track, confirms a safe pause, and asks for manual recovery. It does not automatically skip tracks or restart Apple Music to repair the audio. This policy also applies when ordinary switching/recovery is used without the new restart sequence. With the option off, upstream's existing recovery ladder and repeated-unswitched-track handling remain available.

## Media-session lifetime

Session refresh and detach operations are serialized. Each subscription captures a session epoch; stale callbacks and stale asynchronous metadata/artwork reads cannot update the current item. Native COM identity distinguishes different WinRT wrappers of one session from genuinely replaced sessions; an application ID alone is insufficient.

Fresh checks re-enumerate sessions so an idle, replaced session can be attached again. If the Previous preflight has not dispatched any navigation, it may refresh once and verify the same paused track again. After navigation has been dispatched, the captured session is fixed and cannot be replaced for a retry.

## Limitations and recovery

- GSMTC reports the current item asynchronously. There is no pre-track queue hook here, so pausing at the exact first audio sample cannot be guaranteed. Cold format lookup also occurs before the new pause; an initial fragment before the app obtains mute control remains possible. In the independent pause0.4 test, the user heard about two seconds of the first manually selected track before mute and sample-rate switching began. The user asked to record this known limitation and defer a fix. Its root cause and whether it can be eliminated have not been established; it is not described as inherently unavoidable or proven to occur on every startup.
- Apple Music on the reported setup does not enable external absolute seeking. Previous semantics belong to the player; ten seconds is a conservative experimental threshold, not an Apple guarantee.
- Zero confirmation means a reported timeline position within 0–250 ms, held Paused for 500 ms. This does not guarantee sample-accurate seeking or the first physical sample emitted by the DAC. Repeated queue items with identical metadata may be ambiguous, and native media-session identity does not identify a specific WASAPI stream. Short tracks or unknown/non-advancing progress may not satisfy the restart guard.
- If Previous exposes the known preceding track, one Next correction is allowed only with verified safe queue conditions. Unknown items, shuffle/queue uncertainty, session changes, or unresolved commands stop automatic handling. Remote commands that the player already accepted cannot be withdrawn, so transient navigation cannot be ruled out.
- The mute applies to the entire target output device, including other applications. A pre-existing user mute is preserved.
- Paused/Inactive does not prove that WASAPI released an initialized audio client. Some players or devices may retain an invalidated dormant client after a paused format change. The limited muted recovery can fail; this requires device testing.
- Bit-depth-only changes retain normal behavior and can still interrupt playback. Disabling per-track bit-depth switching selects the configured default bit depth; changing that setting can therefore cause a one-time bit-depth change even between same-rate tracks.

If Previous remains unavailable and the operation can establish that **no navigation command was dispatched**, the expected track stays paused and protectively muted in a **track-scoped hold**. Manually select a different, real track to start fresh handling; repeated Playing notifications for the held track and placeholder metadata cannot release it. To retry the original song, let the different track finish processing before selecting it again.

Unknown identity, a dispatched navigation command with an uncertain result, a command timeout, or failure to confirm the final safe pause retains a **strict global hold**. When this hold or unresolved mute requires restarting the switcher, pause Apple Music first, exit from the system tray, select the intended track, and reopen the switcher. Exiting releases only the mute owned by the switcher. These failure paths deliberately do not invoke the normal skip/restart recovery ladder while the experiment is enabled. The narrowly guarded corrective Next described above remains a separate protection against an already observed wrong-track result.

## Manual recovery UI

The main window and tray flyout expose two separate manual restart actions and categorized failure guidance. Stopped recovery no longer appears as continuous switching; waiting for Play is non-busy. These controls do not enable the experimental option or change its default. See [manual recovery controls](MANUAL_RECOVERY_CONTROLS.md) for pause verification, process handoff, startup gating, and remaining manual checks.

They also share [basic playback controls and English/Traditional Chinese selection](PLAYBACK_CONTROLS_AND_LOCALIZATION.md). Play and navigation do not queue behind guarded processing or a restart. A verified user Pause cancels old work without allowing its cleanup to resume or unmute it; confirmed cancellation has a non-busy status. The display language changes presentation only and does not change the catalog storefront, numeric culture, audio settings, or guarded state classification.

The command tracker also protects the boundary between canceled work and an explicit Pause or manual restart. An accepted Play is not considered observed until Playing is reported. If cancellation or timeout leaves its result uncertain, a later Paused snapshot does not authorize unmute or helper handoff. State observations are tied to command generations, preventing an older read from clearing a newer pending command. This does not retract a command already accepted by the player or make media/session updates atomic.

## Validation

Unit tests use fake playback sessions and endpoints; they do not establish compatibility with every DAC or Apple Music build. They cover default-off behavior, legacy settings compatibility, session identity, bounded commands, stale state propagation, strict zero confirmation, cancellation, preserving mute on uncertain results, deferred playback wakeups, and the distinction between track-scoped and global holds. Formal-source local Release validation is **516 tests passed**, using .NET SDK **8.0.426** on Windows x64. New cases also cover pending Play during user pause/restart, command-generation races, confirmed recovery followed by controls/restart, and language changes that leave playback state untouched.

The independent prototype was tested on Windows x64 build 10.0.26200.0 and TOPPING DX1 II (Windows endpoint `TOPPING USB DAC`). Apple Music 1.1540.23042.0 was recorded earlier but was not queried again during the latest log review; the CPU model and DAC driver/firmware versions were not recorded. The pause0.4 trace contained four rate changes, including both 44.1 → 96 and 96 → 44.1 kHz: each confirmed the selected format, held the same track paused near zero for 500 ms after one Previous, and resumed with an Apple Music session audio peak. Three subsequent same-rate tracks did not enter the new restart sequence. One rate change required the single muted pause/play recovery before succeeding. The user accepted this local test version for contribution.

These are independent-test-build observations, not hardware validation of the precise upstream port. A later isolated bilingual/controls derivative completed 503 local tests and 32 offline UI renders and received limited positive user feedback. None of those results closes the full manual matrix for the formal source. Endpoint formats, session peaks, and reported timelines do not establish what the DAC physically emitted or that every intro was complete. Other devices, ARM64, and other player/OS versions remain unverified. Local results are also separate from GitHub CI.

The user reported two idle/resume failures in an earlier prototype that were consistent with the session-lifetime problems addressed here. Existing traces do not uniquely establish the cause of those failures or demonstrate that every idle, sleep/wake, or device-replacement case is fixed. The following matrix still requires manual validation.

| Manual test | Expected result |
| --- | --- |
| Option off, 44.1 ↔ 96 kHz | Existing live switching and recovery; no new automatic Previous |
| Option on, 44.1 ↔ 96 kHz | Pause before apply; correct target format; muted preparation; intended item starts from its beginning |
| Same-rate tracks, with and without the option | No additional pause/preparation/Previous from this feature |
| User already paused; start monitoring or select a different-rate track, then press Play | No format change or automatic playback before Play; one freshly verified operation after Play |
| Previous naturally remains unavailable | Bounded wait; no forced command; paused/muted track-scoped hold only when no navigation was dispatched; another real track can be processed |
| Bounded audio recovery is exhausted with the option enabled | Confirm safe pause and request manual recovery; no automatic skip or player restart |
| Pause for 15–30 minutes, then manually select a different-rate track | Session refresh works without restarting the switcher; the intended item restarts safely |
| Use another media app while Apple Music is idle, then resume | Only the intended Apple Music session receives commands; no duplicate subscription effects |
| Change tracks or pause during preparation | Old work cancels; no automatic replay of the old item |
| Close/reopen Apple Music or disconnect the device during preparation | Bounded failure or cancellation; no navigation retry on an unverified replacement session |

When reporting results, include the switch direction, whether audio leaked during preparation, whether the intended track remained selected, and whether output began at its start. Review diagnostics for personal information before sharing them.
