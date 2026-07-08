# TriffView

Fast EVE client previews, live log alerts, EVE Settings management, and ESI fleet setup in one standalone app.

TriffView is built for EVE Online multiboxers who want the best parts of preview switching, profile management, alert awareness, EVE Settings copying, and fleet setup assistance without needing multiple programs. It gives you live client previews, rapid character switching, persistent layouts, configurable combat/session alerts, imported EVE-O/EVE-X profiles for a fast transition, EVE Settings backup/copy tools, and an ESI-only Fleet Manager in a focused desktop app that is designed to stay light.

## Why It Exists

Multiboxing should feel immediate. Your preview windows should be where you left them, your hotkeys should follow your characters, and your multiboxing program should not use more resources than your clients.

TriffView is a standalone preview system paired with configurable log alerts, a custom EVE Settings management application, and a fleet layout assistant. One app that can manage how you switch between clients, how you notice important events, how your EVE character settings move between profiles/accounts/backups/fresh installs, and how saved fleet layouts become live ESI fleet invites.

## Highlights

- Live EVE client previews with fast click-to-focus switching.
- Direct character hotkeys and cycle group hotkeys.
- Multiple hotkeys per character or cycle direction.
- Persistent preview layouts per profile.
- Custom preview labels, label placement, label size, opacity, and colors.
- Lockable previews so carefully placed layouts stay put.
- TriffAlerts for attack, warp scramble, decloak, fleet invite, convo request, and system-change awareness.
- Per-alert preview flashes, colors, notifications, and more.
- Client layout save, restore, and optional launch restore.
- Optional inactive-client minimization tuned for rapid switching, no more droppng to desktop or lagging when cycling.
- EVE-O Preview JSON import.
- EVE-X Preview JSON import.
- Full TriffView settings backup export and restore.
- Fleet Manager for saving wing/squad layouts, pre-assigning characters, restructuring live fleets, moving existing members, and sending ESI invites.
- Dark themed standalone settings window with selectable GUI themes.
- Tray controls for quick enable, disable, suspend, save, restore, reload, and quit actions.

## EVE Settings Included

Use it to manage EVE profile and character settings, copy settings, overviews, window positions and layouts between characters, preserve backups, and recover working layouts without digging through EVE's settings folders by hand. For players running multiple accounts or rebuilding setups often, this lets you reload your layouts quickly, and repeatably. 

## Fleet Manager Included

TriffFleets lets you save fleet wing/squad names, pre-assign characters and roles, then hit Apply to restructure your current fleet and invite or move pilots into the correct squads through official EVE SSO and ESI.

Build repeatable fleet templates for DPS, logi, scouts, miners, rolling crews, or whatever your multiboxing setup needs. Create the fleet in-game, detect it with the authenticated fleet boss, then let TriffFleets create or rename wings and squads, move existing members into their saved positions, invite missing characters, keep unexpected pilots in a Bench / Waiting squad, and show a clear result log for every action.

Fleet Manager does not control EVE clients. It does not inject keyboard input, mouse input, chat commands, OCR, memory reads, warps, modules, or invite acceptance. Characters still accept fleet invites manually in-game.

## TriffAlerts Included

TriffAlerts turns EVE's own live game logs into fast, visible preview alerts. When a character takes incoming damage, gets scrammed, decloaks, receives a fleet invite, gets a conversation request, or changes systems, TriffView can flash that character's preview so your eyes go to the right client immediately.

Each alert type has its own color, flash thickness, sound, and notification option. Attack alerts can ignore likely NPC damage so the focus stays on PvP threats. Sounds are packaged with the app, alerts are off by default, and the default mode is quiet and visual: turn it on, tune it once, and let the previews tell you which client needs attention.

TriffAlerts uses EVE log files only. It does not read memory, hook the EVE client, inject input, OCR the screen, or control the game.

## Low Resource By Design

TriffView is built around native Windows preview surfaces instead of a screenshot loop. In normal use, previews stay live without constantly capturing and repainting every client manually.

The result is a preview tool that aims to feel fast, stay smooth during rapid switching, and keep overhead low enough that you never even notice it.

## Profile First

TriffView profiles let different play styles have different layouts.

Keep one profile for fleets, one for market or industry characters, one for scouts, one for testing, or one for every group you fly. Preview positions, labels, hotkeys, cycle groups, colors, client behavior, and imported layouts can all live with the profile they belong to. Switching between cycle groups or profiles can be done hot, with 2 clicks. 

## Imports And Backups

TriffView can import EVE-O Preview and EVE-X Preview JSON settings, then turn them into a TriffView profile. It will replicate your previous layout choices, hotkeys and cycle groups with one click, before letting you customize even further with all the options TriffView offers. It also supports full TriffView settings export and restore, so you can keep a portable backup of every profile and setting.

## Built For EVE Multiboxers

It does not broadcast input, forward input, read game memory, inject into the EVE client, or hook client rendering. It focuses on the things a preview tool should do well: show clients, switch clients, remember layouts, manage settings, assist with ESI fleet invites, and get out of your way.

## Project Notes

TriffView is licensed under the GNU General Public License, version 3 only (`GPL-3.0-only`). See `LICENSE`.

Third-party dependency, font, WebView2, and EVE/CCP trademark notices are listed in `THIRD_PARTY_NOTICES.md`.

## Thanks And Inspiration

- [EVE-O Preview](https://github.com/Proopai/eve-o-preview) for the original live preview/multiboxing inspiration.
- [EVE-MultiPreview](https://github.com/CJKondur/EVE-MultiPreview) for the alerts idea.
- [EVE Settings Manager](https://github.com/mintnick/eve-settings-manager) for the EVE settings management idea.
- [Yeramel](https://www.youtube.com/@Yeramel) for the fleet management tool idea.
