import React, { useEffect, useMemo, useRef, useState } from "react";
import { Pencil } from "lucide-react";
import { clearHudTextFocus, onNativeMessage, postNative } from "../nativeBridge.js";

const EMPTY_STATE = {
  enabled: false,
  hotkeysSuspended: false,
  guideCompleted: true,
  guideVersion: "",
  selectedProfileId: "default",
  profiles: [{ id: "default", name: "Default" }],
  profile: {},
  clients: [],
  hotkeyFailures: [],
  dwmAvailable: true,
};

function send(type, payload = {}) {
  postNative({ type, ...payload });
}

function patchProfile(patch) {
  send("triffview:update-profile", { patch });
}

function Field({ label, children }) {
  return (
    <label className="triffview-field">
      <span>{label}</span>
      {children}
    </label>
  );
}

function Toggle({ label, checked, onChange }) {
  return (
    <label className="triffview-toggle">
      <input type="checkbox" checked={Boolean(checked)} onChange={(event) => onChange(event.target.checked)} />
      <span>{label}</span>
    </label>
  );
}

function DraftControl({
  label,
  value,
  type = "text",
  onCommit,
  live = false,
  parse = (next) => next,
  commitDelay = 350,
  ...props
}) {
  const focusedRef = useRef(false);
  const commitTimerRef = useRef(null);
  const [draft, setDraft] = useState(String(value ?? ""));

  useEffect(() => {
    if (!focusedRef.current) setDraft(String(value ?? ""));
  }, [value]);

  useEffect(() => {
    return () => {
      if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    };
  }, []);

  function commit(next = draft) {
    const parsed = parse(next);
    if (parsed === undefined) return;
    onCommit(parsed);
  }

  function scheduleCommit(next) {
    if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    if (live || commitDelay <= 0) {
      commit(next);
      return;
    }
    commitTimerRef.current = window.setTimeout(() => commit(next), commitDelay);
  }

  return (
    <Field label={label}>
      <input
        {...props}
        type={type}
        value={draft}
        onFocus={() => {
          focusedRef.current = true;
        }}
        onBlur={() => {
          focusedRef.current = false;
          commit();
        }}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            focusedRef.current = false;
            event.currentTarget.blur();
            commit(event.currentTarget.value);
          }
        }}
        onChange={(event) => {
          setDraft(event.target.value);
          scheduleCommit(event.target.value);
        }}
      />
    </Field>
  );
}

function TextAreaSetting({ label, value, onCommit, placeholder }) {
  const focusedRef = useRef(false);
  const commitTimerRef = useRef(null);
  const [draft, setDraft] = useState(value || "");

  useEffect(() => {
    if (!focusedRef.current) setDraft(value || "");
  }, [value]);

  useEffect(() => {
    return () => {
      if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    };
  }, []);

  function scheduleCommit(next) {
    if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    commitTimerRef.current = window.setTimeout(() => onCommit(next), 450);
  }

  return (
    <Field label={label}>
      <textarea
        data-hud-scroll
        className="triffview-textarea"
        value={draft}
        placeholder={placeholder}
        spellCheck="false"
        onFocus={() => {
          focusedRef.current = true;
        }}
        onChange={(event) => {
          setDraft(event.target.value);
          scheduleCommit(event.target.value);
        }}
        onBlur={() => {
          focusedRef.current = false;
          onCommit(draft);
        }}
      />
    </Field>
  );
}

function parseInteger(value) {
  if (String(value).trim() === "") return undefined;
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function clampNumber(value, min, max, fallback = min) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.max(min, Math.min(max, parsed));
}

function SliderControl({ label, value, min, max, step = 1, unit = "", onCommit }) {
  const trackRef = useRef(null);
  const numericValue = clampNumber(value, min, max, min);
  const percent = ((numericValue - min) / Math.max(1, max - min)) * 100;
  const [dragging, setDragging] = useState(false);

  function commit(next) {
    const stepped = Math.round((next - min) / step) * step + min;
    onCommit(clampNumber(stepped, min, max, numericValue));
  }

  function commitFromClientX(clientX) {
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect || rect.width <= 0) return;
    const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    commit(min + ratio * (max - min));
  }

  useEffect(() => {
    if (!dragging) return undefined;

    const onMove = (event) => {
      commitFromClientX(event.clientX);
      event.preventDefault?.();
    };
    const onUp = () => setDragging(false);

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
  }, [dragging, min, max, step, numericValue]);

  return (
    <div className="triffview-field triffview-slider-field">
      <span>{label}</span>
      <div className="triffview-slider-control">
        <button type="button" onClick={() => commit(numericValue - step)}>-</button>
        <div
          ref={trackRef}
          className="triffview-slider-track"
          role="slider"
          tabIndex={0}
          aria-valuemin={min}
          aria-valuemax={max}
          aria-valuenow={numericValue}
          onPointerDown={(event) => {
            setDragging(true);
            commitFromClientX(event.clientX);
            event.preventDefault();
          }}
          onKeyDown={(event) => {
            if (event.key === "ArrowLeft" || event.key === "ArrowDown") {
              event.preventDefault();
              commit(numericValue - step);
            }
            if (event.key === "ArrowRight" || event.key === "ArrowUp") {
              event.preventDefault();
              commit(numericValue + step);
            }
          }}
        >
          <span className="triffview-slider-fill" style={{ width: `${percent}%` }} />
          <span className="triffview-slider-thumb" style={{ left: `${percent}%` }} />
        </div>
        <input
          value={numericValue}
          inputMode="numeric"
          onChange={(event) => commit(event.target.value)}
          aria-label={`${label} value`}
        />
        <span>{unit}</span>
        <button type="button" onClick={() => commit(numericValue + step)}>+</button>
      </div>
    </div>
  );
}

const COLOR_SWATCHES = [
  "#53B6FF",
  "#2EE6A6",
  "#FFCC4D",
  "#FF6B6B",
  "#B58CFF",
  "#D9E2EE",
  "#737B8C",
  "#0B111D",
];

const GUIDE_STEPS = [
  {
    id: "enable",
    label: "Enable",
    title: "Turn on live previews",
    body: "TriffView discovers EVE clients automatically and shows each one as a live DWM preview. Clicking a preview focuses that client; it does not broadcast input, inject into EVE, or read client memory.",
  },
  {
    id: "layout",
    label: "Arrange",
    title: "Place the preview stack",
    body: "Move and resize previews directly on the overlay, then save the positions. Layouts are stored on the selected profile, so the same characters can have different preview layouts in different profiles.",
  },
  {
    id: "lock",
    label: "Lock",
    title: "Lock the layout when it feels right",
    body: "Lock previews keeps accidental drags from moving your saved setup. Settings controls can still change size, opacity, snap, labels, and saved positions.",
  },
  {
    id: "switching",
    label: "Switch",
    title: "Pick a switching style",
    body: "Use direct character hotkeys for one-button focus, or cycle groups for forward/backward swapping. Character Order is the default route for cycle groups and shared same-account hotkeys.",
  },
  {
    id: "clients",
    label: "Clients",
    title: "Save EVE client window positions",
    body: "Client management saves and restores the actual EVE window positions. Auto-restore can apply saved placements when clients appear on launch without constantly fighting later manual movement.",
  },
];

function normalizeHexColor(value) {
  const clean = String(value || "").trim();
  const expanded = clean.replace(/^#?([0-9a-f])([0-9a-f])([0-9a-f])$/i, "#$1$1$2$2$3$3");
  const prefixed = expanded.startsWith("#") ? expanded : `#${expanded}`;
  if (/^#[0-9a-f]{8}$/i.test(prefixed)) return `#${prefixed.slice(3)}`.toUpperCase();
  if (/^#[0-9a-f]{6}$/i.test(prefixed)) return prefixed.toUpperCase();
  return null;
}

function ColorSetting({ label, value, onCommit }) {
  const focusedRef = useRef(false);
  const commitTimerRef = useRef(null);
  const normalized = normalizeHexColor(value) || "#53B6FF";
  const [draft, setDraft] = useState(normalized);

  useEffect(() => {
    if (!focusedRef.current) setDraft(normalized);
  }, [normalized]);

  useEffect(() => {
    return () => {
      if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    };
  }, []);

  function commit(next) {
    const color = normalizeHexColor(next);
    if (!color) return;
    setDraft(color);
    onCommit(color);
  }

  function scheduleCommit(next) {
    if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    commitTimerRef.current = window.setTimeout(() => commit(next), 250);
  }

  return (
    <div className="triffview-field triffview-color-setting">
      <span>{label}</span>
      <div className="triffview-color-control">
        <span className="triffview-color-preview" style={{ background: normalizeHexColor(draft) || normalized }} />
        <input
          value={draft}
          spellCheck="false"
          onFocus={() => {
            focusedRef.current = true;
          }}
          onChange={(event) => {
            setDraft(event.target.value);
            scheduleCommit(event.target.value);
          }}
          onBlur={() => {
            focusedRef.current = false;
            commit(draft);
          }}
        />
      </div>
      <div className="triffview-swatches">
        {COLOR_SWATCHES.map((color) => (
          <button
            type="button"
            key={color}
            title={color}
            aria-label={`Set ${label} to ${color}`}
            style={{ background: color }}
            onClick={() => commit(color)}
          />
        ))}
      </div>
    </div>
  );
}

function splitNames(value) {
  return String(value || "")
    .split(/[\n,]/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function uniqueNames(values) {
  return Array.from(new Set(values.filter(Boolean).map((value) => value.trim()).filter(Boolean)));
}

function namesText(values) {
  return uniqueNames(values).join("\n");
}

function addNamesToText(value, names) {
  return namesText([...splitNames(value), ...names]);
}

function cycleGroupId(name) {
  const clean = String(name || "")
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return clean || `cycle-${Date.now()}`;
}

function splitGestures(value) {
  if (Array.isArray(value)) return uniqueNames(value.map((item) => String(item || "")));
  return uniqueNames(
    String(value || "")
      .split(/[,;\n]/)
      .map((item) => item.trim())
  );
}

function gestureSummary(gestures) {
  const clean = splitGestures(gestures);
  if (!clean.length) return "Unassigned";
  if (clean.length === 1) return clean[0];
  return `${clean[0]} (+${clean.length - 1})`;
}

const BROWSER_KEY_GESTURES = {
  Backspace: "Backspace",
  Tab: "Tab",
  Enter: "Enter",
  Escape: "Escape",
  " ": "Space",
  Spacebar: "Space",
  Insert: "Insert",
  Delete: "Delete",
  Home: "Home",
  End: "End",
  PageUp: "PageUp",
  PageDown: "PageDown",
  ArrowUp: "ArrowUp",
  ArrowDown: "ArrowDown",
  ArrowLeft: "ArrowLeft",
  ArrowRight: "ArrowRight",
  Pause: "Pause",
  CapsLock: "CapsLock",
  PrintScreen: "PrintScreen",
  NumLock: "NumLock",
  ScrollLock: "ScrollLock",
  ContextMenu: "Apps",
  BrowserBack: "BrowserBack",
  GoBack: "BrowserBack",
  BrowserForward: "BrowserForward",
  GoForward: "BrowserForward",
  BrowserRefresh: "BrowserRefresh",
  BrowserStop: "BrowserStop",
  BrowserSearch: "BrowserSearch",
  BrowserFavorites: "BrowserFavorites",
  BrowserHome: "BrowserHome",
  AudioVolumeMute: "VolumeMute",
  VolumeMute: "VolumeMute",
  AudioVolumeDown: "VolumeDown",
  VolumeDown: "VolumeDown",
  AudioVolumeUp: "VolumeUp",
  VolumeUp: "VolumeUp",
  MediaTrackNext: "MediaNextTrack",
  MediaNextTrack: "MediaNextTrack",
  MediaTrackPrevious: "MediaPreviousTrack",
  MediaPreviousTrack: "MediaPreviousTrack",
  MediaStop: "MediaStop",
  MediaPlayPause: "MediaPlayPause",
  LaunchMail: "LaunchMail",
  LaunchMediaPlayer: "SelectMedia",
};

const BROWSER_CODE_GESTURES = {
  Backquote: "Tilde",
  Equal: "Plus",
  Minus: "Minus",
  Comma: "Comma",
  Period: "Period",
  BracketLeft: "[",
  BracketRight: "]",
  Semicolon: "Semicolon",
  Quote: "Quote",
  Slash: "Slash",
  Backslash: "Backslash",
  IntlBackslash: "OEM102",
  NumpadMultiply: "NumPadMultiply",
  NumpadAdd: "NumPadAdd",
  NumpadComma: "NumPadSeparator",
  NumpadSubtract: "NumPadSubtract",
  NumpadDecimal: "NumPadDecimal",
  NumpadDivide: "NumPadDivide",
};

function gestureKeyFromKeyboardEvent(event) {
  if (!event) return "";
  if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return "";

  if (/^Key[A-Z]$/.test(event.code)) return event.code.slice(3);
  if (/^Digit[0-9]$/.test(event.code)) return event.code.slice(5);
  if (/^Numpad[0-9]$/.test(event.code)) return `NumPad${event.code.slice(6)}`;
  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(event.code)) return event.code;

  if (BROWSER_CODE_GESTURES[event.code]) return BROWSER_CODE_GESTURES[event.code];
  if (BROWSER_KEY_GESTURES[event.key]) return BROWSER_KEY_GESTURES[event.key];

  if (event.key?.length === 1) {
    const key = event.key.toUpperCase();
    if (/^[A-Z0-9]$/.test(key)) return key;
    if (event.key === "[") return "[";
    if (event.key === "]") return "]";
    if (event.key === ";") return "Semicolon";
    if (event.key === "/") return "Slash";
    if (event.key === "\\") return "Backslash";
    if (event.key === "'" || event.key === "\"") return "Quote";
  }

  return "";
}

function gestureFromKeyboardEvent(event) {
  const key = gestureKeyFromKeyboardEvent(event);
  if (!key) return "";

  const parts = [];
  if (event.ctrlKey) parts.push("Control");
  if (event.altKey) parts.push("Alt");
  if (event.shiftKey) parts.push("Shift");
  if (event.metaKey) parts.push("Win");
  parts.push(key);
  return parts.join("+");
}

function parseDirectHotkeys(profile) {
  const result = {};

  if (Array.isArray(profile?.directHotkeys)) {
    profile.directHotkeys.forEach((binding) => {
      const name = String(binding?.characterName || "").trim();
      const gestures = splitGestures(binding?.gestures?.length ? binding.gestures : binding?.gesture);
      if (name && gestures.length) result[name] = gestures;
    });
    return result;
  }

  String(profile?.directHotkeysText || "")
    .split(/\r?\n/)
    .forEach((line) => {
      const [name, gestures] = line.split("=").map((part) => part?.trim());
      const cleanGestures = splitGestures(gestures);
      if (name && cleanGestures.length) result[name] = cleanGestures;
    });
  return result;
}

function directHotkeysTextFromMap(map) {
  return Object.entries(map)
    .map(([name, gestures]) => [name, splitGestures(gestures)])
    .filter(([name, gestures]) => name.trim() && gestures.length)
    .map(([name, gestures]) => `${name.trim()} = ${gestures.join(", ")}`)
    .join("\n");
}

function parseCycleGroups(profile) {
  if (Array.isArray(profile?.cycleGroups)) {
    const groups = profile.cycleGroups.map((group) => ({
      id: group.id || cycleGroupId(group.name),
      name: group.name || "Cycle",
      forwardGestures: splitGestures(group.forwardGestures?.length ? group.forwardGestures : group.forwardGesture),
      backwardGestures: splitGestures(group.backwardGestures?.length ? group.backwardGestures : group.backwardGesture),
      charactersText: group.charactersText || "",
      enabled: group.enabled !== false,
    }));
    return groups.length ? groups : [{ id: "all", name: "All", forwardGestures: [], backwardGestures: [], charactersText: "", enabled: true }];
  }

  const groups = String(profile?.cycleGroupsText || "")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      const parts = line.split("|").map((part) => part.trim());
      return {
        id: cycleGroupId(parts[0]),
        name: parts[0] || "Cycle",
        forwardGestures: splitGestures(parts[1] || ""),
        backwardGestures: splitGestures(parts[2] || ""),
        charactersText: splitNames(parts[3] || "").join("\n"),
        enabled: true,
      };
    });
  return groups.length ? groups : [{ id: "all", name: "All", forwardGestures: [], backwardGestures: [], charactersText: "", enabled: true }];
}

function cycleGroupsTextFromGroups(groups) {
  return groups
    .filter((group) => String(group.name || "").trim())
    .map((group) => {
      const members = splitNames(group.charactersText).join(",");
      return [group.name.trim(), splitGestures(group.forwardGestures).join(", "), splitGestures(group.backwardGestures).join(", "), members].join("|");
    })
    .join("\n");
}

function HotkeySummaryButton({ label, gestures, onOpen }) {
  return (
    <div className="triffview-gesture-summary">
      <span className={splitGestures(gestures).length ? "triffview-gesture-value" : "triffview-gesture-value is-empty"}>
        {gestureSummary(gestures)}
      </span>
      <button type="button" onClick={onOpen}>
        {label}
      </button>
    </div>
  );
}

function HotkeyEditorModal({ editor, gestures, recording, onRecord, onClear, onClose }) {
  if (!editor) return null;
  const cleanGestures = splitGestures(gestures);
  const rows = [...cleanGestures, ""];

  return (
    <div className="triffview-modal-backdrop" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget) onClose();
    }}>
      <section className="triffview-hotkey-modal" role="dialog" aria-modal="true" aria-label={editor.label}>
        <header>
          <div>
            <h3>{editor.label}</h3>
            <p>{editor.title}</p>
          </div>
          <button type="button" onClick={onClose} aria-label="Close hotkey editor">
            Close
          </button>
        </header>
        <div className="triffview-hotkey-editor-list">
          {rows.map((gesture, index) => {
            const isRecording = recording
              && recording.type === editor.type
              && recording.id === editor.id
              && recording.field === editor.field
              && recording.index === index;
            return (
              <div className="triffview-hotkey-editor-row" key={`${gesture || "empty"}-${index}`}>
                <span className={gesture ? "triffview-gesture-value" : "triffview-gesture-value is-empty"}>
                  {isRecording ? "Press key combo..." : gesture || "Unassigned"}
                </span>
                <button type="button" onClick={() => onRecord(index)}>
                  {isRecording ? "Cancel" : "Record"}
                </button>
                <button type="button" onClick={() => onClear(index)} disabled={!gesture}>
                  Clear
                </button>
              </div>
            );
          })}
        </div>
      </section>
    </div>
  );
}

function OpacityControl({ value, onCommit }) {
  const percent = Math.round(Math.max(0.2, Math.min(1, Number(value) || 1)) * 100);
  const setPercent = (next) => onCommit(Math.max(20, Math.min(100, next)) / 100);

  return (
    <div className="triffview-field">
      <span>Opacity</span>
      <div className="triffview-stepper">
        <button type="button" onClick={() => setPercent(percent - 5)}>-</button>
        <input
          value={percent}
          inputMode="numeric"
          onChange={(event) => {
            const parsed = parseInteger(event.target.value);
            if (parsed !== undefined) setPercent(parsed);
          }}
        />
        <span>%</span>
        <button type="button" onClick={() => setPercent(percent + 5)}>+</button>
      </div>
    </div>
  );
}

function CharacterListTools({ value, availableNames, onChange, addAllLabel = "Add open characters" }) {
  const currentNames = splitNames(value);
  const currentSet = new Set(currentNames.map((name) => name.toLowerCase()));
  const openNames = availableNames.filter(Boolean);
  const missingNames = openNames.filter((name) => !currentSet.has(name.toLowerCase()));
  if (!openNames.length) return null;

  return (
    <div className="triffview-character-tools">
      <div className="triffview-actions">
        <button type="button" onClick={() => onChange(addNamesToText(value, openNames))}>
          {addAllLabel}
        </button>
        <button type="button" onClick={() => onChange(namesText(openNames))}>
          Use open characters
        </button>
      </div>
      <div className="triffview-character-chips">
        {missingNames.length ? (
          missingNames.map((name) => (
            <button
              type="button"
              key={name}
              onClick={() => onChange(addNamesToText(value, [name]))}
            >
              Add {name}
            </button>
          ))
        ) : (
          <span className="triffview-muted">All open characters are listed.</span>
        )}
      </div>
    </div>
  );
}

function PreviewLabelInput({ value, placeholder, onCommit }) {
  const focusedRef = useRef(false);
  const commitTimerRef = useRef(null);
  const [draft, setDraft] = useState(value || "");

  useEffect(() => {
    if (!focusedRef.current) setDraft(value || "");
  }, [value]);

  useEffect(() => {
    return () => {
      if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    };
  }, []);

  function commit(next = draft) {
    onCommit(String(next || "").trim());
  }

  function scheduleCommit(next) {
    if (commitTimerRef.current) window.clearTimeout(commitTimerRef.current);
    commitTimerRef.current = window.setTimeout(() => commit(next), 350);
  }

  return (
    <input
      value={draft}
      placeholder={placeholder}
      spellCheck="false"
      onFocus={() => {
        focusedRef.current = true;
      }}
      onBlur={() => {
        focusedRef.current = false;
        commit();
      }}
      onKeyDown={(event) => {
        if (event.key === "Enter") {
          event.currentTarget.blur();
          commit(event.currentTarget.value);
        }
        if (event.key === "Escape") {
          setDraft(value || "");
          event.currentTarget.blur();
        }
      }}
      onChange={(event) => {
        setDraft(event.target.value);
        scheduleCommit(event.target.value);
      }}
    />
  );
}

function detectedPreviewName(client) {
  return client.characterName || client.title || client.key || "EVE";
}

function previewLabelKey(client) {
  return client.key || client.characterName || client.title || client.handle;
}

function GuideClientList({ clients }) {
  const visibleClients = clients.slice(0, 5);

  return (
    <div className="triffview-guide-clients">
      <div className="triffview-guide-minihead">
        <span>Detected clients</span>
        <strong>{clients.length}</strong>
      </div>
      {visibleClients.length ? (
        visibleClients.map((client) => (
          <div className="triffview-guide-client" key={client.handle}>
            <span>{client.characterName || client.title}</span>
            <small>{client.foreground ? "active" : client.minimized ? "minimized" : "ready"}</small>
          </div>
        ))
      ) : (
        <p>No EVE clients detected yet.</p>
      )}
      {clients.length > visibleClients.length ? (
        <p>{clients.length - visibleClients.length} more clients detected.</p>
      ) : null}
    </div>
  );
}

function TriffViewGuide({
  clients,
  step,
  setStep,
  onSkip,
  onFinish,
}) {
  const stepIndex = Math.max(0, Math.min(GUIDE_STEPS.length - 1, step));
  const currentStep = GUIDE_STEPS[stepIndex];
  const isFirst = stepIndex === 0;
  const isLast = stepIndex === GUIDE_STEPS.length - 1;

  return (
    <div className="triffview-guide">
      <div className="triffview-guide-progress" aria-label="TriffView guide progress">
        {GUIDE_STEPS.map((item, index) => (
          <div
            key={item.id}
            className={`triffview-guide-step ${index === stepIndex ? "is-active" : index < stepIndex ? "is-complete" : ""}`}
          >
            <span>{index + 1}</span>
            {item.label}
          </div>
        ))}
      </div>

      <div className="triffview-guide-main">
        <div className="triffview-guide-copy">
          <span className="triffview-guide-kicker">Step {stepIndex + 1} of {GUIDE_STEPS.length}</span>
          <h3>{currentStep.title}</h3>
          <p>{currentStep.body}</p>
        </div>

        <GuideClientList clients={clients} />

        <div className="triffview-guide-footer">
          <button type="button" onClick={onSkip}>
            Skip guide
          </button>
          <div className="triffview-guide-next">
            <button type="button" onClick={() => setStep(stepIndex - 1)} disabled={isFirst}>
              Back
            </button>
            {isLast ? (
              <button type="button" className="primary-action" onClick={onFinish}>
                Done
              </button>
            ) : (
              <button type="button" className="primary-action" onClick={() => setStep(stepIndex + 1)}>
                Next
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

function TriffViewSettings({ open = true }) {
  const [state, setState] = useState(EMPTY_STATE);
  const [newProfileName, setNewProfileName] = useState("");
  const [confirmCloseClients, setConfirmCloseClients] = useState(false);
  const [recording, setRecording] = useState(null);
  const [hotkeyEditor, setHotkeyEditor] = useState(null);
  const [activeSection, setActiveSection] = useState("profile");
  const [guideStep, setGuideStep] = useState(0);
  const [editingProfileName, setEditingProfileName] = useState(false);
  const [profileNameDraft, setProfileNameDraft] = useState("");
  const guidePromptedRef = useRef(false);
  const profile = state.profile || {};
  const clients = Array.isArray(state.clients) ? state.clients : [];
  const failures = Array.isArray(state.hotkeyFailures) ? state.hotkeyFailures : [];
  const previewLabels = useMemo(
    () => (profile.previewLabels && typeof profile.previewLabels === "object" && !Array.isArray(profile.previewLabels) ? profile.previewLabels : {}),
    [profile.previewLabels]
  );
  const directHotkeys = useMemo(() => parseDirectHotkeys(profile), [profile]);
  const openCharacterNames = useMemo(
    () => uniqueNames(clients.map((client) => client.characterName || client.title)),
    [clients]
  );
  const characterNames = useMemo(
    () =>
      uniqueNames([
        ...splitNames(profile.characterOrderText),
        ...openCharacterNames,
        ...Object.keys(directHotkeys),
      ]),
    [directHotkeys, openCharacterNames, profile.characterOrderText]
  );
  const cycleGroups = useMemo(() => parseCycleGroups(profile), [profile]);
  const selectedCycleGroup = useMemo(
    () => cycleGroups.find((group) => group.id === profile.selectedCycleGroupId) || cycleGroups[0],
    [cycleGroups, profile.selectedCycleGroupId]
  );
  const status = useMemo(() => {
    if (!state.dwmAvailable) return "DWM unavailable";
    if (!state.enabled) return "Disabled";
    if (clients.length === 0) return "Waiting for EVE clients";
    return `${clients.length} client${clients.length === 1 ? "" : "s"}`;
  }, [clients.length, state.dwmAvailable, state.enabled]);
  const sectionTabs = [
    ["profile", "Profile settings"],
    ["layout", "Preview layout"],
    ["colors", "Color settings"],
    ["hotkeys", "Character Hotkeys"],
    ["cycles", "Cycle Groups and Hotkeys"],
    ["clients", "Client management"],
  ];
  const activeSectionLabel = activeSection === "guide"
    ? "Guide setup"
    : sectionTabs.find(([id]) => id === activeSection)?.[1] || "TriffView";

  useEffect(() => {
    setEditingProfileName(false);
    setProfileNameDraft(profile.name || "");
  }, [profile.id, profile.name]);

  useEffect(() => {
    if (!open || guidePromptedRef.current || state.guideCompleted) return;
    guidePromptedRef.current = true;
    setGuideStep(0);
    setActiveSection("guide");
  }, [open, state.guideCompleted]);

  function updateDirectHotkeys(characterName, gestures) {
    const next = { ...directHotkeys };
    const cleanGestures = splitGestures(gestures);
    if (cleanGestures.length) next[characterName] = cleanGestures;
    else delete next[characterName];
    patchProfile({ directHotkeysText: directHotkeysTextFromMap(next) });
  }

  function updateCycleGroups(nextGroups, selectedId = profile.selectedCycleGroupId) {
    patchProfile({
      cycleGroupsText: cycleGroupsTextFromGroups(nextGroups),
      selectedCycleGroupId: selectedId || nextGroups[0]?.id || "",
    });
  }

  function updateCycleGroup(groupId, patch) {
    const nextGroups = cycleGroups.map((group) => {
      if (group.id !== groupId) return group;
      const next = { ...group, ...patch };
      if (patch.name !== undefined) next.id = cycleGroupId(patch.name);
      return next;
    });
    const selectedId = patch.name !== undefined && profile.selectedCycleGroupId === groupId
      ? cycleGroupId(patch.name)
      : profile.selectedCycleGroupId;
    updateCycleGroups(nextGroups, selectedId);
  }

  function updateCycleGroupHotkeys(groupId, field, gestures) {
    updateCycleGroup(groupId, { [field]: splitGestures(gestures) });
  }

  function editorGestures() {
    if (!hotkeyEditor) return [];
    if (hotkeyEditor.type === "direct") return directHotkeys[hotkeyEditor.id] || [];
    const group = cycleGroups.find((item) => item.id === hotkeyEditor.id);
    return group ? splitGestures(group[hotkeyEditor.field]) : [];
  }

  function updateEditorGestures(gestures) {
    if (!hotkeyEditor) return;
    if (hotkeyEditor.type === "direct") {
      updateDirectHotkeys(hotkeyEditor.id, gestures);
      return;
    }
    updateCycleGroupHotkeys(hotkeyEditor.id, hotkeyEditor.field, gestures);
  }

  function openDirectHotkeyEditor(characterName) {
    setRecording(null);
    setHotkeyEditor({
      type: "direct",
      id: characterName,
      title: characterName,
      label: "Character hotkeys",
    });
  }

  function openCycleHotkeyEditor(group, field) {
    setRecording(null);
    setHotkeyEditor({
      type: "cycle",
      id: group.id,
      field,
      title: group.name,
      label: field === "forwardGestures" ? "Forward hotkeys" : "Backward hotkeys",
    });
  }

  function updatePreviewLabel(key, label) {
    const cleanKey = String(key || "").trim();
    if (!cleanKey) return;
    const cleanLabel = String(label || "").trim();
    const nextLabels = { ...previewLabels };
    if (cleanLabel) nextLabels[cleanKey] = cleanLabel;
    else delete nextLabels[cleanKey];
    patchProfile({ previewLabels: nextLabels });
  }

  function startRecording(nextRecording) {
    if (
      recording
      && recording.type === nextRecording.type
      && recording.id === nextRecording.id
      && recording.field === nextRecording.field
      && recording.index === nextRecording.index
    ) {
      setRecording(null);
      return;
    }
    clearHudTextFocus();
    setRecording(nextRecording);
  }

  function commitProfileName() {
    const nextName = profileNameDraft.trim();
    if (nextName) patchProfile({ name: nextName });
    setEditingProfileName(false);
  }

  function completeGuide() {
    send("triffview:set-guide-completed", { completed: true });
    setActiveSection("profile");
  }

  useEffect(() => {
    if (!recording) return undefined;

    function recordGesture(gesture) {
      if (!gesture) return;

      if (recording.type === "direct") {
        const current = directHotkeys[recording.id] || [];
        const next = [...current];
        next[recording.index ?? current.length] = gesture;
        updateDirectHotkeys(recording.id, next);
      } else if (recording.type === "cycle") {
        const group = cycleGroups.find((item) => item.id === recording.id);
        const current = group ? splitGestures(group[recording.field]) : [];
        const next = [...current];
        next[recording.index ?? current.length] = gesture;
        updateCycleGroupHotkeys(recording.id, recording.field, next);
      }

      setRecording(null);
    }

    function onHudKeyDown(event) {
      const gesture = event.detail?.gesture;
      if (!gesture) return;
      event.preventDefault();
      recordGesture(gesture);
    }

    function onBrowserKeyDown(event) {
      const gesture = gestureFromKeyboardEvent(event);
      if (!gesture) return;
      event.preventDefault();
      event.stopPropagation();
      recordGesture(gesture);
    }

    window.addEventListener("triff:hud-keydown", onHudKeyDown);
    window.addEventListener("keydown", onBrowserKeyDown, true);
    return () => {
      window.removeEventListener("triff:hud-keydown", onHudKeyDown);
      window.removeEventListener("keydown", onBrowserKeyDown, true);
    };
  }, [recording, directHotkeys, cycleGroups, profile.selectedCycleGroupId]);

  useEffect(() => {
    send("triffview:set-settings-open", { open: Boolean(open) });
    return () => send("triffview:set-settings-open", { open: false });
  }, [open]);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffview:state") {
        setState({
          ...EMPTY_STATE,
          ...message,
          profile: message.profile || {},
          clients: Array.isArray(message.clients) ? message.clients : [],
          profiles: Array.isArray(message.profiles) && message.profiles.length ? message.profiles : EMPTY_STATE.profiles,
        });
      }
    });

    return () => {
      unsubscribe();
    };
  }, []);

  useEffect(() => {
    send("triffview:get-state");
  }, []);

  if (activeSection === "guide") {
    return (
      <div className="triffview-settings triffview-settings-guide-mode" data-hud-scroll>
        <section className="triffview-settings-shell triffview-settings-shell-guide">
          <div className="triffview-section-content triffview-guide-content" data-hud-scroll>
            <header className="triffview-section-header">
              <h2>Guide setup</h2>
            </header>
            <TriffViewGuide
              clients={clients}
              step={guideStep}
              setStep={setGuideStep}
              onSkip={completeGuide}
              onFinish={completeGuide}
            />
          </div>
        </section>
      </div>
    );
  }

  return (
    <div className="triffview-settings" data-hud-scroll>
      <section className="triffview-settings-shell">
        <aside className="triffview-side-nav">
          <div className="triffview-nav-brand">
            <h2>TriffView</h2>
            <p>{status}</p>
          </div>
          <nav aria-label="TriffView settings sections">
          {sectionTabs.map(([id, label]) => (
            <button
              type="button"
              key={id}
              className={activeSection === id ? "is-active" : ""}
              onClick={() => setActiveSection(id)}
            >
              {label}
            </button>
          ))}
          </nav>
          <div className="triffview-nav-actions">
            <Toggle label="Lock previews" checked={profile.lockPreviews} onChange={(value) => patchProfile({ lockPreviews: value })} />
            <button type="button" className="primary-action" onClick={() => send("triffview:set-enabled", { enabled: !state.enabled })}>
              {state.enabled ? "Disable" : "Enable"}
            </button>
            <button
              type="button"
              onClick={() => send("triffview:set-hotkeys-suspended", { suspended: !state.hotkeysSuspended })}
              disabled={!state.enabled}
            >
              {state.hotkeysSuspended ? "Resume hotkeys" : "Suspend hotkeys"}
            </button>
            <button
              type="button"
              onClick={() => {
                setGuideStep(0);
                setActiveSection("guide");
              }}
            >
              Guide setup
            </button>
          </div>
        </aside>

        <div className="triffview-section-content" data-hud-scroll>
        <header className="triffview-section-header">
          <h2>{activeSectionLabel}</h2>
        </header>

        {activeSection === "profile" ? (
        <div className="triffview-panel">
          <Field label="Selected profile">
            {editingProfileName ? (
              <div className="triffview-profile-edit-row">
                <input
                  value={profileNameDraft}
                  onChange={(event) => setProfileNameDraft(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") commitProfileName();
                    if (event.key === "Escape") {
                      setProfileNameDraft(profile.name || "");
                      setEditingProfileName(false);
                    }
                  }}
                  autoFocus
                />
                <button type="button" onClick={commitProfileName}>
                  Save
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setProfileNameDraft(profile.name || "");
                    setEditingProfileName(false);
                  }}
                >
                  Cancel
                </button>
              </div>
            ) : (
              <div className="triffview-profile-select-row">
                <select value={state.selectedProfileId || "default"} onChange={(event) => send("triffview:set-profile", { profileId: event.target.value })}>
                  {state.profiles.map((item) => (
                    <option value={item.id} key={item.id}>
                      {item.name}
                    </option>
                  ))}
                </select>
                <button
                  type="button"
                  className="triffview-icon-button"
                  title="Rename selected profile"
                  aria-label="Rename selected profile"
                  onClick={() => {
                    setProfileNameDraft(profile.name || "");
                    setEditingProfileName(true);
                  }}
                >
                  <Pencil size={13} strokeWidth={2} />
                </button>
              </div>
            )}
          </Field>
          <div className="triffview-profile-create">
          <Field label="Create profile">
            <input
              value={newProfileName}
              placeholder="New profile"
              onChange={(event) => setNewProfileName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter" && newProfileName.trim()) {
                  send("triffview:add-profile", { name: newProfileName.trim() });
                  setNewProfileName("");
                }
              }}
            />
          </Field>
          <div className="triffview-row">
            <button
              type="button"
              onClick={() => {
                if (!newProfileName.trim()) return;
                send("triffview:add-profile", { name: newProfileName.trim() });
                setNewProfileName("");
              }}
            >
              Add
            </button>
            <button type="button" onClick={() => send("triffview:delete-profile", { profileId: state.selectedProfileId })}>
              Delete
            </button>
            <button type="button" onClick={() => send("triffview:import-eveo-profile")}>
              Import from EVE-O Preview JSON
            </button>
            <button type="button" onClick={() => send("triffview:import-evex-profile")}>
              Import from EVE-X Preview JSON
            </button>
          </div>
          </div>
          <div className="triffview-profile-create">
            <div className="triffview-backup-note">
              <strong>TriffView backup</strong>
              <span>Restore overwrites all current TriffView data, including every profile, preview layout, client layout, color, hotkey, and enable state.</span>
            </div>
            <div className="triffview-row">
              <button type="button" onClick={() => send("triffview:export-settings-backup")}>
                Export all TriffView settings
              </button>
              <button type="button" onClick={() => send("triffview:restore-settings-backup")}>
                Restore backup and overwrite all
              </button>
            </div>
          </div>
        </div>
        ) : null}

        {activeSection === "layout" ? (
        <div className="triffview-panel">
          <div className="triffview-columns">
            <DraftControl
              label="Width"
              type="number"
              min="120"
              max="1200"
              value={profile.previewWidth || 320}
              parse={parseInteger}
              onCommit={(value) => patchProfile({ previewWidth: value })}
            />
            <DraftControl
              label="Height"
              type="number"
              min="90"
              max="900"
              value={profile.previewHeight || 204}
              parse={parseInteger}
              onCommit={(value) => patchProfile({ previewHeight: value })}
            />
            <OpacityControl value={profile.opacity || 1} onCommit={(value) => patchProfile({ opacity: value })} />
            <DraftControl
              label="Snap px"
              type="number"
              min="0"
              max="80"
              value={profile.snapDistance ?? 18}
              parse={parseInteger}
              onCommit={(value) => patchProfile({ snapDistance: value })}
            />
            <SliderControl
              label="Active border"
              min={1}
              max={16}
              value={profile.borderThickness ?? 2}
              unit="px"
              onCommit={(value) => patchProfile({ borderThickness: value })}
            />
          </div>
          <div className="triffview-toggle-grid">
            <Toggle label="Labels" checked={profile.showLabels} onChange={(value) => patchProfile({ showLabels: value })} />
            <Toggle label="Active highlight" checked={profile.showActiveHighlight} onChange={(value) => patchProfile({ showActiveHighlight: value })} />
            <Toggle label="Inactive borders" checked={profile.showInactiveBorders} onChange={(value) => patchProfile({ showInactiveBorders: value })} />
            <Toggle label="Snap" checked={profile.snapEnabled} onChange={(value) => patchProfile({ snapEnabled: value })} />
            <Toggle label="Hide active preview" checked={profile.hideActivePreview} onChange={(value) => patchProfile({ hideActivePreview: value })} />
            <Toggle label="Hide outside EVE focus" checked={profile.hideOnLostFocus} onChange={(value) => patchProfile({ hideOnLostFocus: value })} />
          </div>
          <div className="triffview-actions">
            <button type="button" onClick={() => send("triffview:save-preview-layout")}>
              Save current positions
            </button>
          </div>
          <div className="triffview-subsection">
            <h4>Preview labels</h4>
            <p className="triffview-muted">
              Replace the text shown in each preview header. Leave blank to use the detected character or window name.
            </p>
            <div className="triffview-label-controls">
              <Toggle
                label="Transparent overlay"
                checked={profile.labelBackgroundTransparent}
                onChange={(value) => patchProfile({
                  labelBackgroundTransparent: value,
                  labelPosition: value || profile.labelPosition !== "center" ? (profile.labelPosition || "top") : "top",
                })}
              />
              <Field label="Label position">
                <select
                  value={profile.labelBackgroundTransparent ? (profile.labelPosition || "top") : (profile.labelPosition === "bottom" ? "bottom" : "top")}
                  onChange={(event) => patchProfile({ labelPosition: event.target.value })}
                >
                  <option value="top">Top</option>
                  <option value="bottom">Bottom</option>
                  <option value="center" disabled={!profile.labelBackgroundTransparent}>Center</option>
                </select>
              </Field>
              <SliderControl
                label="Label size"
                min={8}
                max={32}
                value={profile.labelFontSize ?? 9}
                onCommit={(value) => patchProfile({ labelFontSize: value })}
              />
            </div>
            {!profile.showLabels ? (
              <p className="triffview-muted">Labels are currently hidden. Enable Labels above to show custom preview names.</p>
            ) : null}
            <div className="triffview-label-list">
              {clients.length ? (
                clients.map((client) => {
                  const key = previewLabelKey(client);
                  const detectedName = detectedPreviewName(client);
                  const customLabel = previewLabels[key] || "";
                  return (
                    <div className="triffview-label-row" key={client.handle || key}>
                      <div className="triffview-label-source">
                        <span>{detectedName}</span>
                        <small>{customLabel ? `Displays as ${customLabel}` : "Using detected label"}</small>
                      </div>
                      <PreviewLabelInput
                        value={customLabel}
                        placeholder={detectedName}
                        onCommit={(value) => updatePreviewLabel(key, value)}
                      />
                      <button type="button" onClick={() => updatePreviewLabel(key, "")} disabled={!customLabel}>
                        Reset
                      </button>
                    </div>
                  );
                })
              ) : (
                <p className="triffview-muted">Launch EVE clients to customize preview labels.</p>
              )}
            </div>
          </div>
        </div>
        ) : null}

        {activeSection === "colors" ? (
        <div className="triffview-panel">
          <div className="triffview-color-grid">
            <ColorSetting
              label="Active border"
              value={profile.activeBorderColor || "#53B6FF"}
              onCommit={(value) => patchProfile({ activeBorderColor: value })}
            />
            <ColorSetting
              label="Inactive border"
              value={profile.inactiveBorderColor || "#737B8C"}
              onCommit={(value) => patchProfile({ inactiveBorderColor: value })}
            />
            <ColorSetting
              label="Label text"
              value={profile.labelTextColor || "#D9E2EE"}
              onCommit={(value) => patchProfile({ labelTextColor: value })}
            />
            <ColorSetting
              label="Label fill"
              value={profile.labelBackgroundColor || "#0B111D"}
              onCommit={(value) => patchProfile({ labelBackgroundColor: value })}
            />
          </div>
        </div>
        ) : null}

        {activeSection === "hotkeys" ? (
        <div className="triffview-panel">
          <Toggle
            label="Hotkeys only while EVE is focused"
            checked={profile.hotkeysRequireEveForeground}
            onChange={(value) => patchProfile({ hotkeysRequireEveForeground: value })}
          />
          <div className="triffview-subsection">
            <h4>Direct Hotkeys</h4>
            {characterNames.length ? (
              <div className="triffview-hotkey-list">
                {characterNames.map((characterName) => (
                  <div className="triffview-hotkey-row" key={characterName}>
                    <span>{characterName}</span>
                    <HotkeySummaryButton
                      label="Open hotkeys"
                      gestures={directHotkeys[characterName]}
                      onOpen={() => openDirectHotkeyEditor(characterName)}
                    />
                  </div>
                ))}
              </div>
            ) : (
              <p className="triffview-muted">Launch EVE clients or add Character Order entries to assign direct hotkeys.</p>
            )}
          </div>
          {failures.length ? (
            <div className="triffview-warning">
              {failures.map((failure) => (
                <span key={failure}>{failure}</span>
              ))}
            </div>
          ) : null}
        </div>
        ) : null}

        {activeSection === "cycles" ? (
        <div className="triffview-panel">
          <TextAreaSetting
            label="Character order"
            value={profile.characterOrderText}
            placeholder={"Character One\nCharacter Two"}
            onCommit={(value) => patchProfile({ characterOrderText: value })}
          />
          <CharacterListTools
            value={profile.characterOrderText}
            availableNames={openCharacterNames}
            onChange={(value) => patchProfile({ characterOrderText: value })}
          />
          <div className="triffview-subsection">
            <h4>Cycle Groups</h4>
            <Field label="Active group">
              <select
                value={profile.selectedCycleGroupId || cycleGroups[0]?.id || ""}
                onChange={(event) => patchProfile({ selectedCycleGroupId: event.target.value })}
              >
                {cycleGroups.map((group) => (
                  <option value={group.id} key={group.id}>
                    {group.name}
                  </option>
                ))}
              </select>
            </Field>
            <div className="triffview-actions">
              <button
                type="button"
                onClick={() => {
                  const name = `Group ${cycleGroups.length + 1}`;
                  const nextGroups = [
                    ...cycleGroups,
                    {
                      id: cycleGroupId(name),
                      name,
                      forwardGestures: [],
                      backwardGestures: [],
                      charactersText: "",
                      enabled: true,
                    },
                  ];
                  updateCycleGroups(nextGroups, cycleGroupId(name));
                }}
              >
                Add group
              </button>
            </div>
            {selectedCycleGroup ? (
              <div className="triffview-cycle-card" key={selectedCycleGroup.id}>
                <DraftControl
                  label="Name"
                  value={selectedCycleGroup.name}
                  onCommit={(value) => updateCycleGroup(selectedCycleGroup.id, { name: value })}
                />
                <HotkeySummaryButton
                  label="Open forward hotkeys"
                  gestures={selectedCycleGroup.forwardGestures}
                  onOpen={() => openCycleHotkeyEditor(selectedCycleGroup, "forwardGestures")}
                />
                <HotkeySummaryButton
                  label="Open backward hotkeys"
                  gestures={selectedCycleGroup.backwardGestures}
                  onOpen={() => openCycleHotkeyEditor(selectedCycleGroup, "backwardGestures")}
                />
                <TextAreaSetting
                  label="Members"
                  value={selectedCycleGroup.charactersText}
                  placeholder={"Leave blank for all characters in Character order"}
                  onCommit={(value) => updateCycleGroup(selectedCycleGroup.id, { charactersText: value })}
                />
                <CharacterListTools
                  value={selectedCycleGroup.charactersText}
                  availableNames={openCharacterNames}
                  onChange={(value) => updateCycleGroup(selectedCycleGroup.id, { charactersText: value })}
                  addAllLabel="Add open to group"
                />
                <div className="triffview-actions">
                  <button
                    type="button"
                    onClick={() => {
                      const nextGroups = cycleGroups.filter((item) => item.id !== selectedCycleGroup.id);
                      updateCycleGroups(nextGroups, nextGroups[0]?.id || "");
                    }}
                    disabled={cycleGroups.length <= 1}
                  >
                    Delete group
                  </button>
                </div>
              </div>
            ) : null}
          </div>
          {failures.length ? (
            <div className="triffview-warning">
              {failures.map((failure) => (
                <span key={failure}>{failure}</span>
              ))}
            </div>
          ) : null}
        </div>
        ) : null}

        {activeSection === "clients" ? (
        <>
        <div className="triffview-panel">
          <div className="triffview-toggle-grid">
            <Toggle label="Always maximize on switch" checked={profile.alwaysMaximizeClients} onChange={(value) => patchProfile({ alwaysMaximizeClients: value })} />
            <Toggle label="Auto-restore saved client layouts" checked={profile.autoRestoreClientLayouts} onChange={(value) => patchProfile({ autoRestoreClientLayouts: value })} />
            <Toggle label="Minimize inactive clients" checked={profile.minimizeInactiveClients} onChange={(value) => patchProfile({ minimizeInactiveClients: value })} />
          </div>
          <TextAreaSetting
            label="Do not minimize"
            value={profile.neverMinimizeClientsText}
            placeholder={"Character to keep visible"}
            onCommit={(value) => patchProfile({ neverMinimizeClientsText: value })}
          />
          <div className="triffview-actions">
            <button type="button" onClick={() => send("triffview:save-client-layouts")}>
              Save clients
            </button>
            <button type="button" onClick={() => send("triffview:restore-client-layouts")}>
              Restore clients
            </button>
            <button type="button" onClick={() => setConfirmCloseClients(true)} disabled={!clients.length}>
              Close clients
            </button>
          </div>
          {confirmCloseClients ? (
            <div className="triffview-warning">
              <span>Close all detected EVE client windows?</span>
              <div className="triffview-actions">
                <button type="button" onClick={() => setConfirmCloseClients(false)}>
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={() => {
                    send("triffview:close-clients");
                    setConfirmCloseClients(false);
                  }}
                >
                  Close all
                </button>
              </div>
            </div>
          ) : null}
        </div>

        <div className="triffview-panel">
          <h3>Clients</h3>
          <TextAreaSetting
            label="Hidden previews"
            value={profile.hiddenClientsText}
            placeholder={"Character to hide"}
            onCommit={(value) => patchProfile({ hiddenClientsText: value })}
          />
          <div className="triffview-client-list">
            {clients.length ? (
              clients.map((client) => (
                <div className="triffview-client" key={client.handle}>
                  <span>{client.characterName || client.title}</span>
                  <small>{client.foreground ? "active" : client.minimized ? "minimized" : "ready"}</small>
                </div>
              ))
            ) : (
              <p>No EVE client windows detected.</p>
            )}
          </div>
        </div>
        </>
        ) : null}
        <HotkeyEditorModal
          editor={hotkeyEditor}
          gestures={editorGestures()}
          recording={recording}
          onRecord={(index) => {
            if (!hotkeyEditor) return;
            startRecording({
              type: hotkeyEditor.type,
              id: hotkeyEditor.id,
              field: hotkeyEditor.field,
              index,
            });
          }}
          onClear={(index) => {
            const next = editorGestures().filter((_, gestureIndex) => gestureIndex !== index);
            updateEditorGestures(next);
            if (recording?.index === index) setRecording(null);
          }}
          onClose={() => {
            setHotkeyEditor(null);
            setRecording(null);
          }}
        />
        </div>
      </section>
    </div>
  );
}

export default TriffViewSettings;
