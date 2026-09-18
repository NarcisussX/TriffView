# AutoHotkey window switching

TriffView owns discovery, profiles, character ordering, group membership, direction, target selection, and cycle memory. `TriffViewAhkHotkeys` translates legacy key names, builds command IDs, and passes the resulting AHK expressions to a private AutoHotkey v2 process. The existing controller still selects the HWND and applies its normal cursor, highlight, and minimization behavior.

`F13` stays `F13`. `*F13` stays `*F13`. Wildcards are never added automatically. Legacy bindings such as `Control+Shift+F13` are translated to `^+F13` at registration; settings keep their original spelling. `VK_0xNN`, punctuation names, media keys, and `NoRepeat` remain supported. AHK `vkNN` uses hexadecimal, per AHK syntax. The hotkey editor accepts typed expressions as well as recorded keys. EVE-X imports retain wildcard, mouse, custom-combination, and sided-modifier syntax.

## EVE-X reference

Inspected [`src/Main_Class.ahk` at b5902250f8189f6b3d345b7ba1ac998de6d6b55b](https://github.com/g0nzo83/EVE-X-Preview/blob/b5902250f8189f6b3d345b7ba1ac998de6d6b55b/src/Main_Class.ahk):

- Constructor: 16 ordinary `vkE8` hotkeys covering all Ctrl/Alt/Shift/Win combinations, with `S P1` options.
- `RegisterHotkeys` / `Register_Hotkey_Groups`: dynamic `Hotkey` calls with the configured expression and `HotIf` scope; wildcard syntax is passed to AHK unchanged.
- `Cycle_Hotkey_Groups`: cycles through the configured character order, wraps, and skips missing windows. TriffView keeps its existing cycle implementation and memory instead of copying this state into AHK.
- `ActivateEVEWindow`: restores minimized windows with `ShowWindowAsync`; otherwise uses `SendEvent("{Blind}{vk0xE8}")`.
- `ActivateForgroundWindow`: calls `SetForegroundWindow` from the internal hotkey, retrying once if the API reports failure; optionally maximizes.

`TriffViewHotkeys.ahk` adapts that registration/foreground approach. It uses the equivalent `vkE8` spelling and pointer-sized HWND arguments. It does not use `WinActivate`, synthesize modifier releases/re-presses, or call `AttachThreadInput`. The only generated keystroke is the internal unused virtual key, with `{Blind}`. The EVE-X MIT notice is included in `EVE-X-LICENSE.txt`.

## IPC and lifecycle

A C# message-only window and AHK's hidden script window communicate with Win32 messages; no listening port, shared settings file, or shell-evaluated hotkey code is involved.

1. TriffView starts the bundled runtime with its endpoint HWND and process ID. AHK posts a protocol-v2 ready message. C# verifies that the sender window belongs to the child process.
2. UTF-16 `WM_COPYDATA` messages carry `RESET` (generation, scope, discovered HWNDs), `BIND` (command ID, repeat flag, AHK expression), and `COMMIT` (enabled state). Old registrations are disabled before replacement. Individual rejected bindings appear in the existing hotkey-failure UI.
3. A hotkey posts its command ID and generation to C#. Old generations and unregistered IDs are ignored. C# resolves the direct/cycle command through the existing controller.
4. C# sends the chosen HWND and maximize flag to AHK. AHK performs the EVE-X activation sequence and reports whether that HWND actually became foreground. C# commits or restores its cursor accordingly. Bounded synchronous activation prevents later commands from overtaking a switch; a timed-out helper is stopped before restart.
5. Normal disposal requests AHK exit, with a bounded kill fallback. AHK also watches an open parent-process handle and the IPC window, so it exits after a parent crash. The existing periodic refresh checks helper health and retries failed startup with a delay.

Hotkey-triggered switches use AHK for both initial activation and any reactivation after minimizing the prior client. Thumbnail clicks retain their existing activation path. The .NET `WM_HOTKEY` dispatcher, `RegisterHotKey`/`UnregisterHotKey` imports, modifier-mask parser, and registration-ID bookkeeping have been removed.

## Bundled runtime

- Official AutoHotkey **v2.0.28**, `AutoHotkey64.exe` (unmodified).
- Binary origin: [AutoHotkey_2.0.28.zip](https://github.com/AutoHotkey/AutoHotkey/releases/download/v2.0.28/AutoHotkey_2.0.28.zip).
- Matching source: [v2.0.28 source](https://github.com/AutoHotkey/AutoHotkey/tree/v2.0.28), bundled as `Runtime/AutoHotkey-source.zip`.
- Runtime GPL license: `Runtime/license.txt`.
- SHA-256, executable: `373181727D1AE858564D4DAA678F9FA6CF330D1751F8B284A369C79AFDB05E98`.
- SHA-256, source archive: `CC9C5D38DA5AB83A53F1E22E9E30E377C28F107037730C9C0698381B0129462E`.

The executable, script, licenses, and matching source archive are embedded resources, including in single-file publishing. They are extracted to a content-versioned `%LOCALAPPDATA%\TriffView\Hotkeys` directory and verified against the embedded contents before use. No AHK installation or runtime download is required. The helper has no tray icon or visible UI; runtime errors go to the existing TriffView hotkey-failure UI. The helper is x64; ARM64 Windows requires its x64 emulation support.

## Intentional differences and validation limits

- TriffView keeps its existing independent group cursors, character matching, direct-key sharing for same-account characters, collision reporting, scope settings, and minimize-previous-client behavior. EVE-X's cycle function derives its index from the foreground title and does not provide TriffView's group memory.
- Target selection makes a local IPC round trip. Foreground success is checked explicitly, rather than assuming the activation request worked.
- C# supplies the set of eligible foreground HWNDs. AHK does not maintain its own copy of character/group/profile data. TriffView's existing group-registration policy is retained even when a particular group's members are temporarily offline.
- Settings/profile changes reload in place, rather than requiring EVE-X's settings-close/reload lifecycle.
- `vkE8` is reserved for the internal foreground bridge. Do not run another switcher that binds the same internal virtual key during acceptance testing.
- Native AHK `vkNN` is hexadecimal. Old recorder output (`VK_0xNN`) remains compatible. Settings without the new syntax-version marker migrate legacy decimal `VKNN` to explicit `VK_NN` once, preserving their original key; subsequent AHK expressions retain their hexadecimal meaning.

See [VALIDATION.md](VALIDATION.md) for test results and the outstanding EVE acceptance checks. The user reports successful `*F13` switching with Ctrl, Shift, and Ctrl+Shift held. Ctrl and Shift individually remain effective in the newly selected client, but the combined Unlock Target action requires releasing and pressing Ctrl+Shift again. The user also reports that Shift-before-Ctrl fails within a single client without switching, suggesting a possible key-order dependency; the cause of the switching case remains unresolved. Foreground behavior has been compared to the reference source; a side-by-side gameplay comparison against a running EVE-X instance has not been performed.
