# Playback controls and localization

This branch proposes playback controls and an optional Traditional Chinese interface for Windows Lossless Switcher. It retains jordanmgibson's original project, authorship, GPL license, normal application identity, settings directory, startup registration, and upstream update target. It does not publish an official release. The separate executable and profile used for isolated testing are not included in this contribution.

## Basic controls

The main window and tray flyout share the same controls and operation state:

| Control | Behavior |
| --- | --- |
| Play / Pause | Uses the current Apple Music playback state. Pause stops old processing before confirming a stable pause; Play remains subject to the existing safety holds. |
| Previous | Requests Apple Music's Previous action. Depending on the player's state, it may restart the current track or select the preceding track. It is not a guaranteed seek to zero. |
| Next | Requests the next track when Apple Music enables that action and no conflicting operation is active. |
| Open Apple Music | Requests activation of the existing application, or starts it if closed. It does not close or restart a running player, clear its queue, or press Play. |

These actions use the selected Apple Music media session, rather than sending system-wide media keys. With no usable session, playback commands are disabled and Open Apple Music remains available. The player may temporarily disable individual actions. A successful command result means the request was accepted; the UI does not present that result as proof of audible output or a completed track transition.

## Busy states, pause, and recovery

Play and navigation do not queue behind an active format switch, guarded rewind, or restart. Duplicate clicks do not schedule duplicate operations. Playback controls and manual restart are mutually exclusive, including while Open Apple Music is waiting for the application to appear.

Pause can stop ongoing processing when its result can be verified safely. Old work is canceled without allowing its cleanup to resume playback or release the device mute before confirmation. A confirmed user pause is displayed as a non-busy state. A later Play, either here or in Apple Music, can restart processing of a track whose resolution was canceled.

An already dispatched remote command cannot be withdrawn by canceling a local task. In particular, Play may have been accepted while Apple Music still reports Paused, then take effect later. The coordinator tracks command completion and the expected playback state. A stale observation from an earlier command cannot clear a newer command's pending state. If a prior command is unresolved, Pause and restart preparation retain protection and ask for manual handling instead of declaring a safe pause and unmuting. Timeouts are not treated as permission to resend Play or navigation.

A track-scoped hold after verified zero navigation may allow a supported Next or Previous action to select another real track. Play does not bypass that hold. Unknown identity, unresolved commands, or a global hold block playback and navigation. Follow the displayed recovery advice. If a safe handoff cannot be established, manually pause Apple Music before quitting the tool from the tray and reopening it; exiting releases the tool's owned mute.

See [manual recovery controls](MANUAL_RECOVERY_CONTROLS.md) for the two restart actions. **Restart Apple Music and Tool** requires confirmation and can lose the queue and playback position. Neither basic controls nor language selection enables **Restart track after sample-rate changes (experimental)**; that setting remains **false by default**, including for older settings files.

## Language selection

Open **Settings → Switching → Language** and choose **English** or **繁體中文**. English is the default. The saved JSON preference is:

```json
{
  "uiLanguage": "en"
}
```

Use `"zh-TW"` for Traditional Chinese. Missing or unsupported values fall back to English, as do missing translations. The selection is remembered for the next launch and the restart helper.

Language changes update the open main window, settings, tray flyout, displayed status and activity, and app-owned notifications and dialogs. They do not restart the tool or Apple Music, send media commands, reapply audio settings, clear the catalog cache, or alter the Apple Music storefront or numeric culture. The storefront remains a separate catalog setting.

The translation covers application-owned interface text. Song and artist metadata, device names, and other external data remain as supplied. Raw Windows, driver, or third-party error details can retain their original language. Internal diagnostic statuses and logs remain stable for troubleshooting; translated presentation does not replace the values used for state classification or notification deduplication.

## Validation scope

Formal-source local Release validation: **516 tests passed**, using .NET SDK 8.0.426 on Windows x64. Tests cover control availability, mutual exclusion, cancellation, delayed Play results, stale-reader races, safe handoff refusal, language defaults and persistence, resource coverage, and presentation-only language changes.

The formal source also passed 79 focused tests and 32 offline WPF renders, including live language changes on the same windows, zero binding errors, normal settings availability, and scrolling to the restart controls. Offline rendering uses fake data and does not operate Apple Music or a DAC.

An earlier isolated bilingual/controls derivative completed 503 local tests and 32 offline UI renders. It received positive user feedback in limited use. Those results belong to that derivative and do not establish a complete hardware matrix for this formal source. Local test results also do not establish GitHub CI success.

Remaining real-device checks include both surfaces' controls, restart confirmation/cancel, queue impact, cross-rate and same-rate playback, paused and idle resume, and audible track beginnings. Naturally unreproduced failures remain unverified. The previously reported short audible fragment before the first track is muted remains a recorded limitation; a fix is deferred, and it is not claimed to be inherently unavoidable.
