# Validation

Local tests must not move focus, send keyboard/mouse input, or register live hotkeys on an occupied desktop. The desktop-driving probe runner and its generated executable were removed after it disrupted the user's session. There is no automatic live-input test in the local suite.

## Safe automated checks

Production packaging follow-up (2026-09-17): **247 native tests passed, 0 failed, 0 skipped**, including 21 scram-alert parsing regressions. Vite production output was built separately under `artifacts/release-ui-2.1.0`, preserving the UI folder used by the user's running test build. All 14 local asset references in the packaged UI resolve. Native version metadata is `2.1.0` / `2.1.0.0`.

Signed production artifact: `release/TriffView.exe`, **168,551,824 bytes**, SHA-256 `83E617BB8665D45DA27A2AA40A6AEB828CF458818787CC05FB6DDAC54B4A2B26`. SignTool verification and Authenticode both passed; signer is Cooper Broderick and the Microsoft timestamp is present. Windows Defender's custom file scan completed. The app was not launched, and no desktop/input tests were run. The prior signed v2.0.5 executable is preserved under `artifacts/releases/v2.0.5`. Release notes are in `release/release-notes-v2.1.0.md`; no GitHub release was created.

The following records the earlier unsigned development build:

Final local results (2026-09-17): **226 native tests passed, 0 failed, 0 skipped**, including 47 added tests; **all five alert regression checks passed**; the web production build passed; and a self-contained single-file Windows x64 build published successfully to `artifacts/ahk-local-test/TriffView.exe`. The agent did not launch the resulting application. The test run initially reported a NuGet vulnerability-feed connectivity warning; tests and compilation completed. The packaging check used cached dependencies with vulnerability auditing disabled.

Local build SHA-256: `5BACE28F750462889F4FA2F1DCC6B62D002C26B2B8A2CD757B90B32185A5B2FA`. This is a local unsigned test build; the existing signed release was not replaced.

`native/TriffView.Tests` covers AHK and legacy syntax translation; wildcard and ordinary bindings; settings serialization; imported AHK expressions; direct/group/direction mapping; conflict handling; existing group order and cursor behavior; settings/profile/client-handle reloads; obsolete command rejection; failed registration and commit; suspend/resume; restart configuration; disposal; bundled assets; and missing-runtime errors.

`AhkRuntimeTests` only launches the interpreter with `/validate /ErrorStdOut=UTF-8`. AutoHotkey v2.0.28's `Script::LoadFromFile` returns before script execution when `mValidateThenExit` is set. This parses the real production script without executing `Hotkey`, installing hooks, creating test windows, or sending input. Extraction tests also verify the runtime hash, corresponding source archive, notices, and repair of modified extracted files.

Native and web builds are run without launching the resulting app. Actual process startup/registration/reload/shutdown had passed an earlier runtime check before live testing was stopped; the final local suite deliberately limits runtime execution to syntax validation.

## Changed files

| File | Change |
| --- | --- |
| `native/TriffView/TriffViewSubsystem.cs` | AHK command/activation routing, old hotkey removal, syntax-preserving settings/imports, legacy virtual-key migration |
| `native/TriffView.csproj` | Embedded helper, interpreter, source archive, and notices |
| `app/src/tools/TriffViewSettings.jsx` | Typed AHK expressions in the existing editor |
| `native/Hotkeys/AhkGesture.cs` | Legacy syntax compatibility, expression identity, comma-key preservation |
| `native/Hotkeys/TriffViewAhkHotkeys.cs` | Command mapping, reload, suspension, generations, failures |
| `native/Hotkeys/AhkWindowTransport.cs` | Private IPC endpoint, bounded requests, startup/shutdown, extraction |
| `native/Hotkeys/TriffViewHotkeys.ahk` | AHK registrations and EVE-X foreground bridge |
| `native/Hotkeys/Runtime/AutoHotkey64.exe` | Bundled v2.0.28 interpreter |
| `native/Hotkeys/Runtime/AutoHotkey-source.zip` | Matching upstream source |
| `native/Hotkeys/Runtime/license.txt`, `native/Hotkeys/EVE-X-LICENSE.txt` | Runtime and adaptation notices |
| `native/TriffView.Tests/AhkHotkeyTests.cs`, `native/TriffView.Tests/AhkRuntimeTests.cs` | Safe unit, migration, packaging, and syntax checks |
| `README.md`, `THIRD_PARTY_NOTICES.md`, `native/Hotkeys/README.md`, this file | Usage, licensing, architecture, and validation record |

## User-reported EVE result

On 2026-09-17, the user tested the implementation in EVE and reported that cycling while holding Ctrl works and that Ctrl remains effective in the next client after switching. This is a positive user-observed result for the primary held-Ctrl behavior. The exact configured binding, number of consecutive switches, and full A → B → C click sequence were not recorded. No additional desktop tests were run by the agent in response to this report.

The user subsequently identified the switching binding as `*F13` and reported that Shift also remains effective after switching. Holding Ctrl+Shift does not block switching, but Ctrl+Shift+click does not perform the bound Unlock Target action in the destination client. Releasing and pressing Ctrl+Shift again in that client restores the action.

The user also reports that pressing Shift before Ctrl does not bring up Unlock Target even without switching clients. This supports a possible dependence on key-down order in EVE; the failed action alone does not establish that either modifier's held state was lost or that TriffView reversed their order. Continuously held Ctrl+Shift has not yet been compared with EVE-X. The cause of the combined-action failure remains unresolved. No synthetic modifier replay was added, and no further desktop/input tests were run by the agent.

## Acceptance status and remaining checks

Run these only in a dedicated disposable Windows VM/test session, or on a desktop the user explicitly reserves for testing. Do not assume a hidden window, a normal process sandbox, or a second desktop safely isolates global `SendInput`/`SendEvent`. Windows Sandbox is not installed on the current machine; no VM was installed or opened as part of this work.

| Check | Current result |
| --- | --- |
| `*F14`, Ctrl held, A → B → C with clicks | User reports successful held-Ctrl cycling and Ctrl remaining effective in the next EVE client; exact binding and three-client sequence not recorded |
| Repeated cycling without releasing Ctrl | Held-Ctrl cycling reported by the user; switch count not recorded |
| Shift held through switching | User reports `*F13` switches successfully and Shift remains effective in the next EVE client |
| Ctrl+Shift held through switching | User reports `*F13` switches successfully, but Unlock Target fails until Ctrl+Shift is released and pressed again; Shift-before-Ctrl also fails without switching; cause and EVE-X comparison pending |
| Ordinary `F14` with Ctrl held | Syntax/mapping tested; live behavior pending |
| Wildcard `*F13` with held modifiers | User identifies `*F13`; switching works with Ctrl, Shift, and Ctrl+Shift held; combined Unlock Target action remains unresolved |
| No-modifier cycling | Earlier disposable-window probe passed; EVE validation pending |
| Rapid forward/backward cycling | Cursor/order logic tested; live behavior pending |
| Direct-character hotkeys | Command mapping tested; live behavior pending |
| Change profiles/hotkeys while running | Reload protocol tested with a fake transport; live behavior pending |

For the eventual EVE comparison, run the same ten checks in EVE-X and TriffView separately, with the same group order and scope settings. Confirm the foreground HWND and that clicks in the new client still have the held modifier. Release each modifier only once at the end of its test. Also confirm suspend/resume, minimized target restore, invalid binding feedback, helper exit after normal shutdown, and helper exit after terminating the parent.
