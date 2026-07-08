import React, { Suspense, useEffect, useLayoutEffect, useRef, useState } from "react";
import { onNativeMessage, openExternalUrl, postNative } from "./nativeBridge.js";
import alarmSoundUrl from "./assets/sounds/alarm.ogg";
import dingSoundUrl from "./assets/sounds/ding.ogg";
import sirenSoundUrl from "./assets/sounds/siren.ogg";
import woopSoundUrl from "./assets/sounds/woop.ogg";
import TriffViewSettings from "./tools/TriffViewSettings.jsx";

const EveSettings = React.lazy(() => import("./tools/EveSettings.tsx"));
const TriffFleets = React.lazy(() => import("./tools/TriffFleets.tsx"));

const NAV_ITEMS = [
  { id: "triffview", label: "TriffView" },
  { id: "eve-settings", label: "EVE Settings" },
  { id: "fleet-manager", label: "Fleet Manager" },
];
const THEME_STORAGE_KEY = "triffview.guiTheme";
const GUI_THEMES = [
  { id: "triff-tools", name: "Triff.Tools", swatches: ["#53B6FF", "#2EE6A6", "#0B111D"] },
  { id: "amarr", name: "Amarr", swatches: ["#C8A86A", "#F2E58B", "#F28C57"] },
  { id: "caldari", name: "Caldari", swatches: ["#8FD8E8", "#41B9B2", "#D88B24"] },
  { id: "carbon", name: "Carbon", swatches: ["#D6D6D6", "#8E9297", "#F28C57"] },
  { id: "gallente", name: "Gallente", swatches: ["#55CFA1", "#81BFAE", "#F28C57"] },
  { id: "minmatar", name: "Minmatar", swatches: ["#E65A37", "#9C3F2E", "#F28C57"] },
  { id: "ore", name: "ORE", swatches: ["#F0C515", "#62B2B3", "#D88475"] },
  { id: "servant-sisters", name: "Servant Sisters of EVE", swatches: ["#F15B64", "#A8D8D8", "#BDBB2B"] },
];
const ALERT_SOUND_URLS = {
  alarm: alarmSoundUrl,
  woop: woopSoundUrl,
  siren: sirenSoundUrl,
  ding: dingSoundUrl,
};

function readSavedTheme() {
  try {
    const saved = window.localStorage.getItem(THEME_STORAGE_KEY);
    return GUI_THEMES.some((theme) => theme.id === saved) ? saved : GUI_THEMES[0].id;
  } catch {
    return GUI_THEMES[0].id;
  }
}

function cssVar(style, name) {
  return style.getPropertyValue(name).trim();
}

function currentNativeTheme(theme) {
  const style = window.getComputedStyle(document.documentElement);
  return {
    id: theme.id,
    name: theme.name,
    background: cssVar(style, "--tv-bg"),
    text: cssVar(style, "--tv-text"),
    accent: cssVar(style, "--tv-accent"),
    border: cssVar(style, "--tv-border"),
    panelBorder: cssVar(style, "--tv-border-panel"),
    panelDark: cssVar(style, "--tv-panel-dark"),
    trayImageMargin: cssVar(style, "--tv-topbar-bg"),
    traySelected: cssVar(style, "--tv-accent-bg"),
    trayPressed: cssVar(style, "--tv-accent-selected-bg"),
    caption: cssVar(style, "--tv-topbar-bg"),
    windowBorder: cssVar(style, "--tv-border-topbar"),
    titleText: cssVar(style, "--tv-text"),
  };
}

function publishInputRegions() {
  const regions = Array.from(document.querySelectorAll("[data-hud-input-region]"))
    .map((element) => {
      const rect = element.getBoundingClientRect();
      return {
        x: rect.left,
        y: rect.top,
        width: rect.width,
        height: rect.height,
      };
    })
    .filter((region) => region.width > 0 && region.height > 0);

  postNative({ type: "input-regions", regions });
}

function UpdateNotice({ update, onDismiss, onIgnore }) {
  if (!update?.updateAvailable || update.ignored) return null;

  const versionLabel = update.latestTag || `v${update.latestVersion}`;
  const openUpdate = () => {
    if (!postNative({ type: "update:open" })) {
      openExternalUrl(update.releaseUrl || "https://github.com/NarcisussX/TriffView/releases");
    }
  };

  return (
    <div className="triffview-update-pill" data-hud-input-region="update">
      <span className="triffview-update-copy">
        <strong>Update available</strong>
        <em>{versionLabel}</em>
      </span>
      <button type="button" onClick={openUpdate}>
        Open
      </button>
      <button type="button" onClick={onDismiss}>
        Later
      </button>
      <button type="button" onClick={onIgnore}>
        Ignore
      </button>
    </div>
  );
}

export default function App() {
  const [activeTool, setActiveTool] = useState("triffview");
  const [themeId, setThemeId] = useState(readSavedTheme);
  const [updateInfo, setUpdateInfo] = useState(null);
  const [dismissedUpdateVersion, setDismissedUpdateVersion] = useState("");
  const rootRef = useRef(null);
  const alertAudioReadyRef = useRef(false);
  const playedAlertIdsRef = useRef(new Set());
  const activeTheme = GUI_THEMES.find((theme) => theme.id === themeId) || GUI_THEMES[0];
  const showUpdateNotice =
    updateInfo?.updateAvailable &&
    !updateInfo?.ignored &&
    updateInfo.latestVersion &&
    dismissedUpdateVersion !== updateInfo.latestVersion;

  useEffect(() => {
    if (activeTheme.id !== themeId) {
      setThemeId(activeTheme.id);
    }
  }, [activeTheme.id, themeId]);

  useEffect(() => {
    function playAlertSound(message) {
      const history = Array.isArray(message.alertHistory) ? message.alertHistory : [];
      if (!alertAudioReadyRef.current) {
        history.forEach((alert) => {
          if (alert?.id) playedAlertIdsRef.current.add(alert.id);
        });
        alertAudioReadyRef.current = true;
        return;
      }

      const newest = history[0];
      if (!newest?.id || playedAlertIdsRef.current.has(newest.id)) return;
      playedAlertIdsRef.current.add(newest.id);
      if (playedAlertIdsRef.current.size > 250) {
        playedAlertIdsRef.current = new Set(Array.from(playedAlertIdsRef.current).slice(-160));
      }

      const alerts = message.alerts && typeof message.alerts === "object" ? message.alerts : {};
      const events = alerts.events && typeof alerts.events === "object" ? alerts.events : {};
      const config = events[newest.type] || {};
      const soundUrl = ALERT_SOUND_URLS[config.sound || "none"];
      if (!soundUrl) return;

      const audio = new Audio(soundUrl);
      audio.volume = Math.max(0, Math.min(1, Number(alerts.masterVolume ?? 0.75)));
      audio.play().catch(() => {});
    }

    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "standalone:navigate") {
        const tool = String(message.tool || "");
        if (NAV_ITEMS.some((item) => item.id === tool)) {
          setActiveTool(tool);
        }
      } else if (message?.type === "update-state") {
        setUpdateInfo(message.update || null);
      } else if (message?.type === "triffview:state") {
        playAlertSound(message);
      }
    });

    postNative({ type: "standalone:ready" });
    return unsubscribe;
  }, []);

  useEffect(() => {
    const frame = window.requestAnimationFrame(publishInputRegions);
    return () => window.cancelAnimationFrame(frame);
  }, [activeTool, themeId]);

  useLayoutEffect(() => {
    document.documentElement.dataset.theme = activeTheme.id;
    document.documentElement.dataset.themeName = activeTheme.name;
    postNative({ type: "standalone:set-theme", theme: currentNativeTheme(activeTheme) });
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, activeTheme.id);
    } catch {
      // Theme persistence is best-effort; the selected theme still applies for this session.
    }
  }, [activeTheme]);

  useEffect(() => {
    publishInputRegions();
    window.addEventListener("resize", publishInputRegions);
    const observer = new ResizeObserver(() => publishInputRegions());
    if (rootRef.current) observer.observe(rootRef.current);
    return () => {
      window.removeEventListener("resize", publishInputRegions);
      observer.disconnect();
    };
  }, []);

  return (
    <main className="triffview-standalone-root hud-root" data-theme={activeTheme.id} data-theme-name={activeTheme.name} ref={rootRef}>
      <header className="triffview-standalone-topbar" data-hud-input-region="topbar">
        <div className="triffview-brand-block">
          <strong>TriffView</strong>
          <span>Previews, fleets, and EVE settings</span>
          <label className="triffview-theme-picker">
            <span className="triffview-theme-swatches" aria-hidden="true">
              {activeTheme.swatches.map((color) => (
                <i key={color} style={{ backgroundColor: color }} />
              ))}
            </span>
            <select
              className="triffview-theme-select"
              value={activeTheme.id}
              onChange={(event) => setThemeId(event.target.value)}
              aria-label="GUI theme"
            >
              {GUI_THEMES.map((theme) => (
                <option key={theme.id} value={theme.id}>
                  {theme.name}
                </option>
              ))}
            </select>
          </label>
        </div>
        {showUpdateNotice ? (
          <UpdateNotice
            update={updateInfo}
            onDismiss={() => setDismissedUpdateVersion(updateInfo.latestVersion)}
            onIgnore={() => {
              postNative({ type: "update:ignore-version", version: updateInfo.latestVersion });
              setDismissedUpdateVersion(updateInfo.latestVersion);
            }}
          />
        ) : null}
        <nav aria-label="Standalone tool navigation">
          {NAV_ITEMS.map((item) => (
            <button
              key={item.id}
              type="button"
              className={activeTool === item.id ? "is-active" : ""}
              onClick={() => setActiveTool(item.id)}
            >
              {item.label}
            </button>
          ))}
        </nav>
      </header>

      <section className="triffview-standalone-panel" data-hud-input-region="panel">
        {activeTool === "triffview" ? <TriffViewSettings open /> : null}
        {activeTool === "eve-settings" ? (
          <Suspense fallback={<div className="triffview-standalone-loading">Loading EVE Settings...</div>}>
            <EveSettings />
          </Suspense>
        ) : null}
        {activeTool === "fleet-manager" ? (
          <Suspense fallback={<div className="triffview-standalone-loading">Loading Fleet Manager...</div>}>
            <TriffFleets />
          </Suspense>
        ) : null}
      </section>
    </main>
  );
}
