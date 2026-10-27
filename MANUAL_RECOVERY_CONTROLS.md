# Manual recovery controls

The main window and tray flyout provide **Restart Tool** and **Restart Apple Music and Tool**. These are the English labels, without an ellipsis; the optional Traditional Chinese interface translates them. Both actions are available independently of the experimental track-restart setting, whose default remains off.

The same surfaces share Play/Pause, Previous, Next, and Open Apple Music commands. See [playback controls and localization](PLAYBACK_CONTROLS_AND_LOCALIZATION.md) for their usage, capability checks, language selection, and recovery boundaries.

## Failure guidance

Recovery states carry a `RecoveryCategory` alongside the existing diagnostic reason. A stopped recovery and unavailable Previous control are displayed as failures requiring attention, rather than remaining in the generic switching phase. Waiting for the user's Play is a normal, non-busy state.

| State | Suggested action |
| --- | --- |
| Previous unavailable with verified zero navigation | Select a different track; restarting is usually unnecessary |
| Bounded audio recovery exhausted | Select another track; consider manually restarting both applications if several tracks remain silent |
| Unknown track/session or uncertain command result | Pause Apple Music before restarting the tool |
| Missing output device | Check the DAC connection and selected endpoint |
| Unsupported or rejected format, or unstable format confirmation | Check device capabilities and format settings first |
| Missing track format | Check metadata/network availability or select a different track |
| Waiting for Play | Leave the device unchanged and wait for the user |

The advice remains visible in the main window and tray flyout. A non-blocking tray notification is deduplicated by track and failure category; it is separate from optional successful-format notifications. Guidance does not execute a restart. The existing normal recovery settings still apply when the experimental option is off; the experiment's bounded, no-skip/no-player-restart failure policy remains unchanged.

## Restart sequence

1. Claim both restart commands and disable conflicting playback controls so repeated clicks cannot schedule multiple handoffs. A playback action already in progress, including opening the player, prevents a restart from starting. Cancelling the combined-restart confirmation does not stop processing or issue media commands.
2. Suspend new work, cancel superseded work, and acquire the switching barrier. Check that no earlier playback command remains unresolved, then confirm Apple Music is Paused, Stopped, or Closed, or positively establish that the player is absent. A rejected, unknown, cancelled, or unconfirmed pause does not authorize shutdown. A Paused snapshot also cannot authorize shutdown while an earlier Play may still execute.
3. Start a helper using the current application executable. Validate the request's profile, path, nonce, age, parent PID/start time, and parent executable. Publish readiness only after closing the ready-file writer.
4. Recheck the pause after the helper becomes ready. Explicitly await coordinator cleanup before WPF shutdown releases the instance lock and owned mute.
5. The helper waits for the old process to exit, up to 30 seconds. It does not kill an unresponsive switcher or launch a second controller while the old one remains alive.
6. For **Restart Tool**, relaunch only the current tool executable. For **Restart Apple Music and Tool**, the user first confirms the queue impact; after the old tool exits, the existing player controller attempts graceful closure, terminates the player after its grace period if necessary, clears a surviving media agent, and reopens Apple Music before the tool.
7. The new tool verifies paused playback before starting monitoring and waits for user Play. This temporary startup gate also works with the experimental option off and does not modify the persisted setting. The restart flow does not restore a queue or send Play, Next, Previous, or Seek.

An uncertain player-restart timeout does not authorize launching the tool on top of a potentially pending remote operation. A definite player-restart failure can reopen the tool with a failure notice. Startup that cannot confirm a safe pause shows manual instructions without starting automatic switching.

## Pending commands and user pause

The coordinator distinguishes a completed local wait from an observed playback effect. An accepted Play stays pending until Playing is observed; cancellation or timeout can leave the remote result uncertain. Command bookkeeping uses a lock and generation checks, so an older Paused read cannot erase a newer Play request, including when the expected state later has the same enum value.

A user Pause cancels old processing and prevents its automatic Play or mute cleanup from overriding the user's action. It releases the tool's owned mute only after a stable pause and the absence of unresolved commands are confirmed. Successful cancellation is shown as a non-busy paused state, preserving the current track and actual device format. If an earlier command may still execute, Pause and restart preparation refuse a safe handoff, retain protection, and show manual instructions. They do not issue extra Play, Pause, or navigation commands merely to make an uncertain result appear settled.

## Limitations and validation

Restarting Apple Music can lose its playback queue and position. Restarting the tool only rebuilds its own state and session connections; it cannot guarantee repair of a player renderer or unsupported device format. Already accepted remote operations cannot be withdrawn. A fully frozen UI may prevent both sets of buttons from working; this feature does not add a resident external watchdog.

Formal-source local Release validation: **516 tests passed**, using .NET SDK **8.0.426** on Windows x64. Coverage includes readiness handoff, control/restart mutual exclusion, confirmed user pause, delayed Play and cancellation, stale-reader races, and both language settings.

Tests use fake playback/process operations, plus temporary-file validation for the readiness handoff. The formal source passed 79 focused tests and 32 offline WPF renders, with live language switching, zero binding errors, and verified scrolling to the restart controls. Rendering uses fake data; it does not launch the switcher, change DAC formats, or operate Apple Music. A separate isolated bilingual/controls derivative completed 503 tests and 32 offline UI renders and received limited positive user feedback. That feedback does not close every restart, queue, notification, idle-resume, or audible-output check for the precise formal source. Earlier independent-build observations remain separate evidence, and local passes are not a claim of GitHub CI success.

| Manual check | Expected result |
| --- | --- |
| Restart Tool from either surface | One new copy of the same executable; Apple Music remains open |
| Combined restart, then Cancel | No pause or restart side effects |
| Confirm combined restart | Player and tool reopen; user selects the track and presses Play |
| User presses Play while helper starts | Pause is rechecked before owned mute can be released |
| Pause rejected or never confirmed | Tool stays open with instructions; no blind shutdown/unmute |
| Old process does not exit | Helper stops without killing it or launching another copy |
| Restart with the experimental option off | Format unchanged until Play; option remains off |
| Repeated failure callbacks | One notification per track/category and persistent advice |
| Playback or Open action already running | Restart cannot overlap it; no duplicate launch or transport operation |
| Earlier Play is pending or its result is uncertain | Pause/restart does not release mute or start a helper based on a stale Paused snapshot |
| User Pause cancels resolution | Stable pause is non-busy; track and current format remain visible; later Play starts fresh handling |
