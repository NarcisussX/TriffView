import React, { useEffect, useMemo, useRef, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";

type Readiness = "Ready" | "Training" | "Missing";

type SkillRequirement = {
  skillName: string;
  level: number;
};

type SkillCharacter = {
  characterId: number;
  characterName: string;
  fetchedUtc: string;
  error: string;
  needsReauth: boolean;
};

type SkillPlanSummary = {
  name: string;
  requirementCount: number;
};

type MatrixEntry = {
  characterId: number;
  planName: string;
  readiness: Readiness;
  estimatedFinishUtc: string | null;
  missingSkills: SkillRequirement[];
  unknownSkills: string[];
};

type TriffSkillsState = {
  authConfigured: boolean;
  characters: SkillCharacter[];
  plans: SkillPlanSummary[];
  matrix: MatrixEntry[];
  refreshInFlight: boolean;
  authInProgress: boolean;
  plansUpdatedUtc: string;
};

// One selection drives the detail panel, whichever of the three affordances set
// it - a cell, a plan row header, or a character column header.
type Selection =
  | { kind: "cell"; characterId: number; planName: string }
  | { kind: "plan"; planName: string }
  | { kind: "character"; characterId: number }
  | null;

// Selecting what is already selected clears it, so every affordance that opens
// the detail panel also closes it. Without this the panel is a one-way door:
// there is no "no selection" target to click once one is set.
function sameSelection(a: Selection, b: Selection) {
  if (!a || !b || a.kind !== b.kind) return false;
  if (a.kind === "cell" && b.kind === "cell") {
    return a.characterId === b.characterId && a.planName === b.planName;
  }
  if (a.kind === "plan" && b.kind === "plan") return a.planName === b.planName;
  if (a.kind === "character" && b.kind === "character") return a.characterId === b.characterId;
  return false;
}

type DetailRow = {
  key: string;
  label: string;
  entry: MatrixEntry | null;
};

// A client-side echo of SkillPlanParser.Parse (native). The controller writes the
// clipboard text verbatim, so this only drives the preview - but it mirrors the
// native rules exactly so the "N skills parsed" count matches what will load.
type ImportPreview = { count: number; lines: string[] };

const PLAN_ROMAN_LEVELS: Record<string, number> = { I: 1, II: 2, III: 3, IV: 4, V: 5 };

// The same table read the other way, for display. EVE writes skill levels in Roman
// numerals and so do the plan .txt files the user typed, so "Gunnery 4" on screen
// is a translation the reader has to run on every line to check it against a game
// window that says "Gunnery IV".
//
// Anything outside I-V falls back to the digits rather than inventing a numeral: a
// hand-written plan file can name any integer, and an honest 7 is better than a
// wrong VII the parser would never have produced.
const ROMAN_LEVELS = ["", "I", "II", "III", "IV", "V"];

function levelLabel(level: number) {
  return ROMAN_LEVELS[level] || String(level);
}

function parsePlanPreview(text: string): ImportPreview | null {
  const order: string[] = [];
  const display = new Map<string, string>();
  const levels = new Map<string, number>();

  for (const rawLine of (text || "").split("\n")) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#")) continue;

    const lastSpace = line.lastIndexOf(" ");
    if (lastSpace < 0) continue;

    // trimEnd, the 1-5 bound, and case-insensitive merging all match the native
    // parser - without them the preview counts skills the load would drop.
    const skillName = line.slice(0, lastSpace).trimEnd();
    const token = line.slice(lastSpace + 1).toUpperCase();
    let level: number | null = null;
    if (Object.prototype.hasOwnProperty.call(PLAN_ROMAN_LEVELS, token)) {
      level = PLAN_ROMAN_LEVELS[token];
    } else if (/^[1-5]$/.test(token)) {
      level = Number(token);
    }
    if (level === null) continue;

    const key = skillName.toLowerCase();
    const existing = levels.get(key);
    if (existing === undefined) {
      order.push(key);
      display.set(key, skillName);
    }
    if (existing === undefined || level > existing) levels.set(key, level);
  }

  if (!order.length) return null;
  return {
    count: order.length,
    lines: order.slice(0, 5).map((key) => `${display.get(key)} ${levelLabel(levels.get(key) ?? 0)}`),
  };
}

const WINDOWS_RESERVED_PLAN_NAMES = new Set([
  "CON",
  "PRN",
  "AUX",
  "NUL",
  "COM1",
  "COM2",
  "COM3",
  "COM4",
  "COM5",
  "COM6",
  "COM7",
  "COM8",
  "COM9",
  "LPT1",
  "LPT2",
  "LPT3",
  "LPT4",
  "LPT5",
  "LPT6",
  "LPT7",
  "LPT8",
  "LPT9",
]);

const MAX_PLAN_NAME_LENGTH = 120;

// Advisory only, same reasoning as parsePlanPreview above: a friendly message as the
// user types, not the boundary. PlanNameValidator (native) runs the
// authoritative version of these same rules against the actual web message, because
// a renderer-side check can't be trusted to have run at all.
function planNameHint(name: string): string {
  if (!name) return "";
  if (name.length > MAX_PLAN_NAME_LENGTH) return `Name is too long (max ${MAX_PLAN_NAME_LENGTH} characters).`;
  if (name !== name.trim()) return "Name can't start or end with a space.";
  if (name.endsWith(".")) return "Name can't end with a period.";
  if (/[\\/:*?"<>|]/.test(name) || name.includes("..")) {
    return `Name can't contain \\ / : * ? " < > | or "..".`;
  }
  const stem = name.split(".")[0];
  if (WINDOWS_RESERVED_PLAN_NAMES.has(stem.toUpperCase())) {
    return `"${stem}" is a reserved Windows device name.`;
  }
  return "";
}

const EMPTY_STATE: TriffSkillsState = {
  authConfigured: false,
  characters: [],
  plans: [],
  matrix: [],
  refreshInFlight: false,
  authInProgress: false,
  plansUpdatedUtc: "",
};

const READINESS_META: Record<Readiness, { label: string; className: string }> = {
  Ready: { label: "Ready", className: "is-ready" },
  Training: { label: "Training", className: "is-training" },
  Missing: { label: "Missing", className: "is-missing" },
};

const READINESS_ORDER: Readiness[] = ["Ready", "Training", "Missing"];

type SortDirection = "none" | "desc" | "asc";

function nextSort(current: SortDirection): SortDirection {
  if (current === "none") return "desc";
  if (current === "desc") return "asc";
  return "none";
}

const SORT_LABEL: Record<SortDirection, string> = {
  none: "folder order",
  desc: "most ready first",
  asc: "fewest ready first",
};

// Geometric arrows rather than the caret glyphs, which render at wildly
// different weights across the fonts WebView2 falls back to.
const SORT_ARROW: Record<SortDirection, string> = {
  none: "↕",
  desc: "↓",
  asc: "↑",
};

// No entry for a character x plan pair at all - the pair has not been scored.
const UNSCORED_META = { label: "Not scored", className: "is-unscored" };

// An entry whose readiness is outside the three known strings. Its own class, not
// the unscored one: "we asked and got an answer we cannot read" and "we never
// asked" have different fixes, and a shared mark hid that they were different.
const UNKNOWN_META = { label: "Unknown", className: "is-unknown" };

// The one readiness symbol, used in the grid, the legend, the group headings and
// the cell detail. It carries no state of its own: fill and colour come from the
// --tv-fill and color of the enclosing is-ready / is-training / is-missing class
// sets, so a single CSS rule changes the vocabulary everywhere at once.
function Mark() {
  return <span className="triffskills-mark" aria-hidden="true" />;
}

// Two channels, one question each. Fill answers "how far along", and is always
// the fraction of the plan already trained. Colour answers "does the gap close
// on its own": muted for Missing, accent for Training, success for Ready.
//
// Quantised to quarters because at 11px a continuous ramp reads as noise rather
// than as steps, and capped below 1 for anything short of Ready so a full box
// only ever means done. Inverting it this way also takes the loudest colour off
// the most common state: on a fresh roster most pairs are Missing, and a wall of
// red says nothing except that the wall is red.
const FILL_STEPS = [0, 0.25, 0.5, 0.75];

function fillFor(entry: MatrixEntry | null, requirementCount: number): number | undefined {
  if (!entry || !READINESS_META[entry.readiness]) return undefined;
  if (entry.readiness === "Ready") return 1;
  if (requirementCount <= 0) return 0;
  // Unresolved skills count as untrained, not trained: a plan whose names all
  // failed to resolve is 0% toward ready, not nearly full.
  const missing = (entry.missingSkills || []).length + (entry.unknownSkills || []).length;
  const trained = Math.max(0, requirementCount - missing) / requirementCount;
  return FILL_STEPS[Math.min(FILL_STEPS.length - 1, Math.floor(trained * FILL_STEPS.length))];
}

// Spoken form of the same fraction, so the aria-label carries what the fill
// carries. Screen readers get "Missing, a quarter trained", not just "Missing".
const FILL_PHRASE: Record<string, string> = {
  "0": "none trained",
  "0.25": "under half trained",
  "0.5": "about half trained",
  "0.75": "nearly trained",
};

function fillStyle(fill: number | undefined): React.CSSProperties | undefined {
  if (fill === undefined) return undefined;
  return { "--tv-fill": String(fill) } as React.CSSProperties;
}

const REAUTH_HINT =
  "Needs re-authentication for esi-skills.read_skills.v1 and esi-skills.read_skillqueue.v1. Use Add character to reauthorize.";

// Above this the detail sits beside the grid rather than under it. The number is
// the point at which a 30-column matrix and a readable skill list stop competing
// for the same horizontal space: below it the rail would starve both.
const RAIL_QUERY = "(min-width: 1000px)";
const DETAIL_MIN = 148;
const DETAIL_MAX = 560;
const DETAIL_NUDGE = 24;

// PageUp/PageDown jump. Ten rows is enough that a forty-plan list is four presses
// rather than forty, and small enough that the reader still lands somewhere they
// recognise instead of being teleported.
const PAGE_ROWS = 10;

// Header geometry. Flat headers need a column per name; rotated ones need a band
// tall enough to stand the name up in.
const FLAT_COL_PAD = 16; // 8px each side, matching .is-flat-heads .triffskills-head-button
const FLAT_TOTAL_EXTRA = 12; // the totals column widens from 44px to 56px to hold "Ready" laid flat
const FLAT_HEAD_H = 28;
const ROTATED_HEAD_MIN = 56;
const ROTATED_HEAD_MAX = 168; // ~30 characters; past this the band costs more rows than the name is worth
const ROTATED_HEAD_PAD = 14;

// Reserved whether or not a vertical scrollbar is currently there. Without it the
// header mode and the scrollbar can drive each other: going flat shortens the
// header, which can remove the scrollbar, which widens the box, which can allow
// flat... The reserve costs one column at the margin and settles it for good.
const SCROLLBAR_RESERVE = 16;

// One canvas for the process. measureText against the header's own computed font
// gives the real advance width before anything is on screen, so the flat-or-rotated
// decision happens in the same render rather than by laying out flat and watching
// for overflow, which the user would see as a flicker on every resize.
let measureContext: CanvasRenderingContext2D | null = null;

function textWidth(text: string, font: string) {
  if (!measureContext) measureContext = document.createElement("canvas").getContext("2d");
  // A refused 2D context is not worth a fallback estimate: a wrong width here
  // silently picks the wrong layout. Report "unmeasurable" and let the caller
  // keep the rotated headers, which fit at any roster size.
  if (!measureContext) return null;
  measureContext.font = font;
  return measureContext.measureText(text).width;
}

// Returns postNative's own true/false (bridge present or not) so callers that
// track "waiting for a reply" state - startClipboardImport is the one that
// currently matters - can clear it immediately on a false return instead of
// waiting forever for a reply that was never going to arrive.
function send(type: string, payload: Record<string, unknown> = {}) {
  return postNative({ type, ...payload });
}

function formatUtc(value?: string | null) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function matrixKey(characterId: number, planName: string) {
  return `${characterId}\u0000${planName}`;
}

function indexMatrix(entries: MatrixEntry[]) {
  const index = new Map<string, MatrixEntry>();
  for (const entry of entries || []) {
    index.set(matrixKey(entry.characterId, entry.planName), entry);
  }
  return index;
}

function isDegraded(character: SkillCharacter) {
  return Boolean(character.error) || Boolean(character.needsReauth);
}

// A readiness value outside the three known strings is an anomaly, not a
// confident "Missing" - route it to the same unscored/unknown vocabulary the
// null-entry branch uses, rather than silently reading as Missing.
function bucketOf(entry: MatrixEntry | null): Readiness | null {
  if (!entry) return null;
  return READINESS_META[entry.readiness] ? entry.readiness : null;
}

function metaFor(entry: MatrixEntry | null) {
  if (!entry) return UNSCORED_META;
  return READINESS_META[entry.readiness] ?? UNKNOWN_META;
}

// One-line gist of an entry, used by the grouped lists in the detail panel.
function summarize(entry: MatrixEntry | null) {
  const bucket = bucketOf(entry);
  if (!entry) return "No result yet - use Refresh characters";
  if (bucket === "Ready") return "All requirements met";
  if (bucket === "Training") {
    const eta = formatUtc(entry.estimatedFinishUtc);
    return eta ? `Done ${eta}` : "ETA unknown (queue paused)";
  }
  if (bucket === "Missing") {
    const count = (entry.missingSkills || []).length;
    return count === 1 ? "1 skill missing" : `${count} skills missing`;
  }
  return "Unrecognised readiness value";
}

// The first few skills standing in the way, named rather than counted. A count
// says how much work is left; the names say whether it is the work you were
// going to do anyway. Three, because a fourth pushes the entry onto a third line
// and the list stops scanning.
const PREVIEW_LIMIT = 3;

function previewMissing(entry: MatrixEntry | null) {
  const missing = entry?.missingSkills || [];
  if (!missing.length) return "";
  const named = missing.slice(0, PREVIEW_LIMIT).map((skill) => `${skill.skillName} ${levelLabel(skill.level)}`);
  const rest = missing.length - named.length;
  return rest > 0 ? `${named.join(", ")}, +${rest} more` : named.join(", ");
}

// "2 skills missing" above "Gunnery 4, Drones 5" says two twice. When the names
// are the whole list, they replace the count; when they are a sample, the count
// is what tells you how much is out of frame.
function summaryLine(entry: MatrixEntry | null) {
  const missing = entry?.missingSkills || [];
  if (bucketOf(entry) === "Missing" && missing.length && missing.length <= PREVIEW_LIMIT) return "";
  return summarize(entry);
}

// How far this pair is from flyable, on one scale so every group sorts the same
// way: nearest first. Ready is already there. Training is ordered by when the
// queue finishes, with a paused queue (no ETA) sorted last within its group
// because it finishes never. Missing is ordered by how many skills are left.
//
// Last is a large finite number, not Infinity: two paused queues would subtract
// to NaN, and a NaN comparator leaves the sort order undefined.
const SORTS_LAST = Number.MAX_SAFE_INTEGER;

function distanceToReady(entry: MatrixEntry | null): number {
  const bucket = bucketOf(entry);
  if (bucket === "Ready") return 0;
  if (bucket === "Training") {
    const finish = entry?.estimatedFinishUtc ? Date.parse(entry.estimatedFinishUtc) : NaN;
    return Number.isNaN(finish) ? SORTS_LAST : finish;
  }
  return (entry?.missingSkills || []).length;
}

// Buckets in READINESS_ORDER, then unscored and unrecognised as separate groups.
// Within each, nearest to ready first, so the top of every list is the answer to
// "who is closest" and the reader never has to scan the whole group to find it.
// Empty groups are dropped.
function groupRows(rows: DetailRow[]) {
  const groups: { key: string; meta: { label: string; className: string }; rows: DetailRow[] }[] = [];
  const byDistance = (a: DetailRow, b: DetailRow) => distanceToReady(a.entry) - distanceToReady(b.entry);
  for (const readiness of READINESS_ORDER) {
    const members = rows.filter((row) => bucketOf(row.entry) === readiness);
    if (members.length) groups.push({ key: readiness, meta: READINESS_META[readiness], rows: members.sort(byDistance) });
  }
  const unscored = rows.filter((row) => bucketOf(row.entry) === null && !row.entry);
  if (unscored.length) groups.push({ key: "Unscored", meta: UNSCORED_META, rows: unscored });
  const unknown = rows.filter((row) => bucketOf(row.entry) === null && !!row.entry);
  if (unknown.length) groups.push({ key: "Unknown", meta: UNKNOWN_META, rows: unknown });
  return groups;
}

export default function TriffSkills() {
  const [state, setState] = useState<TriffSkillsState>(EMPTY_STATE);
  const [error, setError] = useState("");
  const [confirmForgetId, setConfirmForgetId] = useState(0);
  const [selection, setSelection] = useState<Selection>(null);

  // Sorting is presentation only and deliberately not persisted: the folder order
  // is the order the user chose when naming the files, so it is the resting state
  // to come back to. Cycles desc -> asc -> none.
  const [planSort, setPlanSort] = useState<SortDirection>("none");
  const [characterSort, setCharacterSort] = useState<SortDirection>("none");

  // Where the detail sits. Beside the grid when there is room for both, under it
  // otherwise. Tracked in state rather than left to CSS because the drag handle
  // has to know which axis it is on.
  const [isRail, setIsRail] = useState(
    () => typeof window !== "undefined" && window.matchMedia(RAIL_QUERY).matches,
  );

  useEffect(() => {
    const query = window.matchMedia(RAIL_QUERY);
    const onChange = (event: MediaQueryListEvent) => setIsRail(event.matches);
    setIsRail(query.matches);
    query.addEventListener("change", onChange);
    return () => query.removeEventListener("change", onChange);
  }, []);

  // One control, two remembered numbers: a rail wants width, a band wants height,
  // and a single value would make every breakpoint change discard the user's
  // choice. Not persisted, for the same reason the sorts are not.
  const [detailSize, setDetailSize] = useState({ rail: 320, band: 220 });
  const dragRef = useRef<{ origin: number; base: number } | null>(null);
  const currentSize = isRail ? detailSize.rail : detailSize.band;

  const resizeDetail = (next: number) => {
    const clamped = Math.max(DETAIL_MIN, Math.min(DETAIL_MAX, Math.round(next)));
    setDetailSize((current) => (isRail ? { ...current, rail: clamped } : { ...current, band: clamped }));
  };

  const startDrag = (event: React.PointerEvent<HTMLDivElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    dragRef.current = { origin: isRail ? event.clientX : event.clientY, base: currentSize };
  };

  // The panel follows the handle on both axes, so it grows as the pointer moves
  // back towards the start of the axis.
  const moveDrag = (event: React.PointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current;
    if (!drag) return;
    resizeDetail(drag.base + (drag.origin - (isRail ? event.clientX : event.clientY)));
  };

  const endDrag = (event: React.PointerEvent<HTMLDivElement>) => {
    dragRef.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  };

  const nudgeDrag = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const grow = isRail ? "ArrowLeft" : "ArrowUp";
    const shrink = isRail ? "ArrowRight" : "ArrowDown";
    if (event.key !== grow && event.key !== shrink) return;
    event.preventDefault();
    resizeDetail(currentSize + (event.key === grow ? DETAIL_NUDGE : -DETAIL_NUDGE));
  };

  // Set when the user presses "Import from clipboard" and cleared the moment a
  // "clipboard" reply is consumed. read-clipboard/clipboard is a generic pair every
  // tool shares, so without this flag a clipboard event some other tool caused
  // would open this dialog too.
  const [pendingClipboardImport, setPendingClipboardImport] = useState(false);
  const [importDraft, setImportDraft] = useState<{ contents: string; preview: ImportPreview } | null>(null);
  const [importName, setImportName] = useState("");
  const [importCollision, setImportCollision] = useState(false);
  const [importSubmitError, setImportSubmitError] = useState("");
  const [importBusy, setImportBusy] = useState(false);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffskills:state") {
        // Built field by field rather than spread. A spread carries the transport-only
        // "type" key into component state, and - worse - a present-but-null field wins
        // over the EMPTY_STATE default it is supposed to fall back to, so one null
        // characters array turns every .map/.length below into a crash. Every field the
        // renderer reads is normalized here, once, instead of guarded at each use.
        setState({
          authConfigured: message.authConfigured === true,
          characters: Array.isArray(message.characters) ? message.characters : [],
          plans: Array.isArray(message.plans) ? message.plans : [],
          matrix: Array.isArray(message.matrix) ? message.matrix : [],
          refreshInFlight: message.refreshInFlight === true,
          authInProgress: message.authInProgress === true,
          plansUpdatedUtc: typeof message.plansUpdatedUtc === "string" ? message.plansUpdatedUtc : "",
        });
      }
      if (message?.type === "triffskills:error") {
        // import-plan errors are rendered inside the import modal; showing them in
        // this banner too would display the same failure twice.
        if (message.action === "import-plan") return;
        setError(`${message.action || "TriffSkills"}: ${message.message || "Unknown error"}`);
      }
    });

    send("triffskills:get-state");
    return unsubscribe;
  }, []);

  const cells = useMemo(() => indexMatrix(state.matrix), [state.matrix]);
  const charactersById = useMemo(
    () => new Map(state.characters.map((character) => [character.characterId, character])),
    [state.characters],
  );
  const plansByName = useMemo(() => new Map(state.plans.map((plan) => [plan.name, plan])), [state.plans]);
  const hasMatrix = state.characters.length > 0 && state.plans.length > 0;

  // Margin totals and the sorts that read from them. A matrix this size is
  // scanned, not read, and the two questions a scan actually asks - which plans
  // are covered, which characters are useful - are answered by the margins
  // rather than by any individual cell.
  const planReady = useMemo(() => {
    const totals = new Map<string, number>();
    for (const plan of state.plans) {
      let ready = 0;
      for (const character of state.characters) {
        if (cells.get(matrixKey(character.characterId, plan.name))?.readiness === "Ready") ready += 1;
      }
      totals.set(plan.name, ready);
    }
    return totals;
  }, [state.plans, state.characters, cells]);

  const characterReady = useMemo(() => {
    const totals = new Map<number, number>();
    for (const character of state.characters) {
      let ready = 0;
      for (const plan of state.plans) {
        if (cells.get(matrixKey(character.characterId, plan.name))?.readiness === "Ready") ready += 1;
      }
      totals.set(character.characterId, ready);
    }
    return totals;
  }, [state.plans, state.characters, cells]);

  // The corner where the two margins meet counts the same thing they do, so the
  // whole L reads on one scale: pairs already flyable.
  const readyTotal = useMemo(() => {
    let total = 0;
    for (const value of characterReady.values()) total += value;
    return total;
  }, [characterReady]);

  const orderedPlans = useMemo(() => {
    if (planSort === "none") return state.plans;
    const factor = planSort === "desc" ? -1 : 1;
    return [...state.plans].sort(
      (a, b) =>
        factor * ((planReady.get(a.name) ?? 0) - (planReady.get(b.name) ?? 0)) || a.name.localeCompare(b.name),
    );
  }, [state.plans, planSort, planReady]);

  const orderedCharacters = useMemo(() => {
    if (characterSort === "none") return state.characters;
    const factor = characterSort === "desc" ? -1 : 1;
    return [...state.characters].sort(
      (a, b) =>
        factor * ((characterReady.get(a.characterId) ?? 0) - (characterReady.get(b.characterId) ?? 0)) ||
        a.characterName.localeCompare(b.characterName),
    );
  }, [state.characters, characterSort, characterReady]);

  // The grid is one tab stop - exactly one cell carries tabIndex 0 and the rest
  // carry -1 - and the arrows move the cursor inside it; per-cell tab stops would
  // put a thousand of them between the first cell and the next control.
  // Native table semantics are kept rather than role="grid": a screen reader in
  // browse mode takes the arrows for its own table navigation, which is better than
  // anything below; this is for the sighted keyboard user.
  const [cursor, setCursor] = useState({ row: 0, col: 0 });
  const gridBodyRef = useRef<HTMLTableSectionElement | null>(null);

  // Character names lie flat while they fit the grid's width and stand on end when
  // they do not. Rotation is a cost paid for density, and a four-character roster
  // buys nothing with it. The measurement decides rather than a character count,
  // because it is name length and available width, not how many characters there
  // are, that governs whether the names fit.
  const gridRef = useRef<HTMLTableElement | null>(null);
  // A callback ref rather than useRef + effect: the grid is behind hasMatrix and
  // appears after the first commit, so an empty-deps effect would run while the node
  // is still null and never observe it. State setters are stable, so this attaches
  // exactly once, when the node actually appears.
  const [gridScrollNode, setGridScrollNode] = useState<HTMLDivElement | null>(null);
  const [gridWidth, setGridWidth] = useState(0);
  // Webfont metrics differ from the fallback's, so a measurement taken before
  // Rajdhani has loaded measured the wrong font. This re-runs it once it has.
  const [fontsReady, setFontsReady] = useState(false);

  useEffect(() => {
    let live = true;
    document.fonts?.ready.then(() => {
      if (live) setFontsReady(true);
    });
    return () => {
      live = false;
    };
  }, []);

  // The grid pane is a flex item with min-width 0, so its width is set by the split
  // and never by the table inside it. That is what makes this safe to observe: the
  // header mode changes the content width, not the box being measured.
  useEffect(() => {
    if (!gridScrollNode) return;
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (entry) setGridWidth(entry.contentRect.width);
    });
    observer.observe(gridScrollNode);
    return () => observer.disconnect();
  }, [gridScrollNode]);

  const headLayout = useMemo(() => {
    // Rotated is the fallback for every uncertain case: it fits at any roster size,
    // so being wrong in this direction costs legibility, while being wrong the other
    // way pushes characters off the right edge.
    const rotated = (widest: number) => ({
      flat: null as number[] | null,
      height: Math.round(Math.min(ROTATED_HEAD_MAX, Math.max(ROTATED_HEAD_MIN, widest + ROTATED_HEAD_PAD))),
    });

    const table = gridRef.current;
    if (!table || !orderedCharacters.length) return rotated(0);

    const style = window.getComputedStyle(table);
    const font = `800 ${style.fontSize} ${style.fontFamily}`;
    const widths: number[] = [];
    for (const character of orderedCharacters) {
      const width = textWidth(character.characterName, font);
      if (width === null) return rotated(0);
      widths.push(width);
    }
    const widest = Math.max(...widths);

    // The two frozen columns are read off the table rather than from the width
    // tokens, so the narrow breakpoint that changes them needs no second copy of
    // this arithmetic.
    const frozen = Array.from(
      table.querySelectorAll("thead .triffskills-grid-corner, thead .triffskills-grid-total-head"),
    ).reduce((sum, cell) => sum + cell.getBoundingClientRect().width, 0);

    const flat = widths.map((width) => Math.ceil(width) + FLAT_COL_PAD);
    const needed = frozen + FLAT_TOTAL_EXTRA + flat.reduce((sum, width) => sum + width, 0);
    if (!gridWidth || needed > gridWidth - SCROLLBAR_RESERVE) return rotated(widest);
    return { flat, height: FLAT_HEAD_H };
    // fontsReady is not read here: it is in the list to re-run the measurement
    // against the real font once it has loaded.
  }, [orderedCharacters, gridWidth, fontsReady]);

  // Clamped every render rather than pruned by an effect, for the same reason the
  // selection below is: sorting, a forgotten character, or a deleted plan file can
  // all shrink the grid under a cursor that was in range when it was set.
  const rowMax = Math.max(0, orderedPlans.length - 1);
  const colMax = Math.max(0, orderedCharacters.length - 1);
  const cursorRow = Math.min(cursor.row, rowMax);
  const cursorCol = Math.min(cursor.col, colMax);

  // Focus moves imperatively here rather than in an effect keyed on the cursor. An
  // effect would also run on mount and on every clamp, dragging focus into the grid
  // from wherever the user actually was.
  function moveCursor(row: number, col: number) {
    const nextRow = Math.max(0, Math.min(rowMax, row));
    const nextCol = Math.max(0, Math.min(colMax, col));
    setCursor({ row: nextRow, col: nextCol });
    gridBodyRef.current
      ?.querySelector<HTMLButtonElement>(`[data-grid-row="${nextRow}"][data-grid-col="${nextCol}"]`)
      ?.focus();
  }

  // Delegated from the body rather than bound per cell, and driven by the focused
  // cell's own coordinates rather than by the cursor state, so a key pressed before
  // React has re-rendered still moves from where the caret visibly is.
  function onGridKeyDown(event: React.KeyboardEvent<HTMLTableSectionElement>) {
    const data = (event.target as HTMLElement).dataset;
    const row = Number(data?.gridRow);
    const col = Number(data?.gridCol);
    if (!Number.isInteger(row) || !Number.isInteger(col)) return;

    let nextRow = row;
    let nextCol = col;
    switch (event.key) {
      case "ArrowUp":
        nextRow = row - 1;
        break;
      case "ArrowDown":
        nextRow = row + 1;
        break;
      case "ArrowLeft":
        nextCol = col - 1;
        break;
      case "ArrowRight":
        nextCol = col + 1;
        break;
      case "PageUp":
        nextRow = row - PAGE_ROWS;
        break;
      case "PageDown":
        nextRow = row + PAGE_ROWS;
        break;
      case "Home":
        nextCol = 0;
        if (event.ctrlKey) nextRow = 0;
        break;
      case "End":
        nextCol = colMax;
        if (event.ctrlKey) nextRow = rowMax;
        break;
      default:
        return;
    }
    // Without this the scroll container also acts on the same key, so every arrow
    // press would move the cursor one cell and the viewport some other distance.
    event.preventDefault();
    moveCursor(nextRow, nextCol);
  }

  // The selection is resolved against the live state on every render rather
  // than pruned by an effect, so a character forgotten or a plan file deleted
  // elsewhere simply stops resolving instead of leaving a dangling panel.
  const selectedCharacter =
    selection && selection.kind !== "plan" ? charactersById.get(selection.characterId) || null : null;
  const selectedPlan = selection && selection.kind !== "character" ? plansByName.get(selection.planName) || null : null;

  function select(next: Selection) {
    setSelection((current) => (sameSelection(current, next) ? null : next));
    setConfirmForgetId(0);
  }

  function clearSelection() {
    setSelection(null);
    setConfirmForgetId(0);
  }

  // Escape is the third way out, and the one keyboard users reach for. Cells are
  // buttons reached by tab, so a mouse-only dismiss would leave them in the same
  // dead end. Bound only while something is selected, so it never swallows an
  // Escape the rest of the app might want - and skipped entirely while the import
  // modal is open, so its own Escape handler below is the only one that fires
  // instead of both firing off the same keydown and closing the modal *and*
  // clearing the grid selection behind it.
  useEffect(() => {
    if (!selection || importDraft) return;
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      setSelection(null);
      setConfirmForgetId(0);
    }
    // Escape pressed while the EVE client has focus is forwarded here as a
    // triff:hud-keydown CustomEvent, not a keydown (nativeBridge.js's own
    // Escape handling just blurs the focused control otherwise) - same
    // dual-listener pattern as TriffViewSettings.jsx's hotkey recorder.
    function onHudKeyDown(event: Event) {
      const detail = (event as CustomEvent).detail;
      if (detail?.key !== "Escape") return;
      event.preventDefault();
      setSelection(null);
      setConfirmForgetId(0);
    }
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("triff:hud-keydown", onHudKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("triff:hud-keydown", onHudKeyDown);
    };
  }, [selection, importDraft]);

  function confirmForget(characterId: number) {
    send("triffskills:forget-character", { characterId });
    setConfirmForgetId(0);
    setSelection(null);
  }

  function startClipboardImport() {
    setPendingClipboardImport(true);
    // No bridge (dev/browser today) means no "clipboard" reply is ever coming,
    // so the button would otherwise sit on "Reading clipboard..." forever with
    // no way to retry. In-app this is unreachable - ReadClipboard always replies.
    if (!send("read-clipboard")) setPendingClipboardImport(false);
  }

  function closeImportModal() {
    setImportDraft(null);
    setImportName("");
    setImportCollision(false);
    setImportSubmitError("");
    setImportBusy(false);
  }

  // Editing the name clears the collision flag: the flag answers "does THIS name
  // collide", and Replace sends whatever name is currently in the box, so a stale
  // flag from a previous name must never leave Replace showing. A fresh submit is
  // what decides whether the new name collides.
  function changeImportName(value: string) {
    setImportName(value);
    setImportCollision(false);
    setImportSubmitError("");
  }

  function submitImport(replace: boolean) {
    if (!importDraft) return;
    const trimmedName = importName.trim();
    if (!trimmedName) return;
    setImportBusy(true);
    setImportSubmitError("");
    setImportCollision(false);
    // Same guard as startClipboardImport: with no bridge the reply that clears
    // importBusy never arrives, leaving the dialog stuck on "Importing...".
    if (!send("triffskills:import-plan", { name: trimmedName, contents: importDraft.contents, replace })) {
      setImportBusy(false);
    }
  }

  // The "clipboard" reply is broadcast to every tool. Listening only while
  // pendingClipboardImport is set - and clearing it the moment a reply arrives - is
  // what stops a clipboard read some other tool triggered from opening this dialog.
  useEffect(() => {
    if (!pendingClipboardImport) return;
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type !== "clipboard") return;
      setPendingClipboardImport(false);
      const text = typeof message.text === "string" ? message.text : "";
      const preview = parsePlanPreview(text);
      if (!preview) {
        setError(
          'TriffSkills: Clipboard did not look like a skill plan. Expected one skill per line, name then level (e.g. "Caldari Frigate III").',
        );
        return;
      }
      setImportDraft({ contents: text, preview });
      setImportName("");
      setImportCollision(false);
      setImportSubmitError("");
      setImportBusy(false);
    });
    return unsubscribe;
  }, [pendingClipboardImport]);

  // triffskills:import-collision, triffskills:import-done, and the "import-plan"
  // triffskills:error all only ever arrive in response to the triffskills:import-plan
  // message this modal itself sends, so - same reasoning as the clipboard listener
  // above - this only listens while there is a draft open to react to.
  useEffect(() => {
    if (!importDraft) return;
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "triffskills:import-collision") {
        setImportBusy(false);
        setImportCollision(true);
        return;
      }
      if (message?.type === "triffskills:import-done") {
        closeImportModal();
        return;
      }
      if (message?.type === "triffskills:error" && message.action === "import-plan") {
        setImportBusy(false);
        setImportSubmitError(message.message || "Could not import the plan.");
      }
    });
    return unsubscribe;
  }, [importDraft]);

  // Bound only while the import modal is open, so Escape closes the modal instead
  // of falling through to the grid-selection handler above (which is itself
  // skipped for exactly this reason while importDraft is set).
  useEffect(() => {
    if (!importDraft) return;
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      closeImportModal();
    }
    // Same triff:hud-keydown gap as the selection handler above, and the same
    // fix: without this, Escape forwarded from the EVE client never reaches a
    // plain keydown listener at all - nativeBridge.js just blurs the input.
    function onHudKeyDown(event: Event) {
      const detail = (event as CustomEvent).detail;
      if (detail?.key !== "Escape") return;
      event.preventDefault();
      closeImportModal();
    }
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("triff:hud-keydown", onHudKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("triff:hud-keydown", onHudKeyDown);
    };
  }, [importDraft]);

  return (
    <div className="triffview-settings triffskills" data-hud-scroll data-hud-select-text-controls="true">
      <section className="triffview-settings-shell">
        <aside className="triffview-side-nav">
          <div className="triffview-nav-brand">
            <h2>TriffSkills</h2>
            <p>
              {state.characters.length} characters / {state.plans.length} plans
            </p>
          </div>
          <div className="triffview-nav-actions">
            <button
              type="button"
              className="primary-action"
              onClick={() => send("triffskills:auth")}
              disabled={state.authInProgress}
            >
              {state.authInProgress ? "Waiting for EVE SSO..." : "Add character"}
            </button>
            <button
              type="button"
              onClick={() => send("triffskills:refresh-characters")}
              disabled={state.refreshInFlight || !state.characters.length}
            >
              {state.refreshInFlight ? "Refreshing..." : "Refresh characters"}
            </button>
            <button type="button" onClick={() => send("triffskills:open-plans-folder")}>
              Open plans folder
            </button>
            <button type="button" onClick={() => send("triffskills:refresh-plans")}>
              Reload plans
            </button>
            <button type="button" onClick={startClipboardImport} disabled={pendingClipboardImport}>
              {pendingClipboardImport ? "Reading clipboard..." : "Import from clipboard"}
            </button>
          </div>
          {!state.authConfigured ? (
            <div className="triffview-warning">
              <strong>SSO client ID missing.</strong>
              <span>Set the built-in TriffView EVE SSO client ID before authenticating a character.</span>
            </div>
          ) : null}
          <div className="triffskills-legend">
            {READINESS_ORDER.map((key) => (
              <span key={key} className={READINESS_META[key].className}>
                <Mark />
                {READINESS_META[key].label}
              </span>
            ))}
            <span className={UNSCORED_META.className}>
              <Mark />
              {UNSCORED_META.label}
            </span>
            {/* The marks above teach the three colours by showing them. Neither of
                these two facts can be shown that way: the fill varies per cell, so
                no fixed sample states the rule, and a keyboard affordance has no
                symbol at all. The margin totals are not repeated here - the section
                header already says what they count. */}
            <p className="triffskills-legend-note">Fill is the share of the plan already trained.</p>
            <p className="triffskills-legend-note">Arrow keys move between cells.</p>
          </div>
        </aside>

        <div className="triffview-section-content" data-hud-scroll>
          <header className="triffview-section-header">
            <div>
              <h2>Skill plan readiness</h2>
              <p>
                Rows are plans, columns are characters. The margins count what is ready. Pick a cell, a plan or a
                character for the detail.
              </p>
            </div>
            <span className="triffskills-plans-stamp">
              {state.plansUpdatedUtc ? `Plans updated ${formatUtc(state.plansUpdatedUtc)}` : "No plans yet"}
            </span>
          </header>

          {error ? (
            <div className="triffview-warning triffskills-error">
              <strong>TriffSkills</strong>
              <span>{error}</span>
              <button type="button" onClick={() => setError("")}>
                Clear
              </button>
            </div>
          ) : null}

          {!state.characters.length ? (
            <div className="eve-settings-empty">
              No characters yet. Use <strong>Add character</strong> to authorize one through EVE SSO. TriffSkills
              requests its own skill scopes and stores its own refresh token; it never reads Fleet Manager&apos;s.
            </div>
          ) : null}

          {!state.plans.length ? (
            <div className="eve-settings-empty">
              No plans yet. TriffSkills scores your characters against the plan files in{" "}
              <code>%APPDATA%\TriffHud\TriffSkills\plans</code>. Use <strong>Open plans folder</strong> to
              get there, drop in one <code>.txt</code> per plan (one skill per line, name then level,
              as in <em>Navigation V</em>), then use <strong>Reload plans</strong>.
            </div>
          ) : null}

          {hasMatrix ? (
            <>
              <div
                className={isRail ? "triffskills-split is-rail" : "triffskills-split"}
                style={{ "--tv-detail-size": `${currentSize}px` } as React.CSSProperties}
              >
                <div className="triffskills-grid-scroll" data-hud-scroll ref={setGridScrollNode}>
                  <table
                    className={headLayout.flat ? "triffskills-grid is-flat-heads" : "triffskills-grid"}
                    ref={gridRef}
                    style={{ "--tv-skills-head-h": `${headLayout.height}px` } as React.CSSProperties}
                  >
                    <colgroup>
                      <col className="triffskills-grid-label-col" />
                      <col className="triffskills-grid-total-col" />
                      {orderedCharacters.map((character, index) => (
                        <col
                          key={character.characterId}
                          className="triffskills-grid-cell-col"
                          style={headLayout.flat ? { width: `${headLayout.flat[index]}px` } : undefined}
                        />
                      ))}
                    </colgroup>
                    <thead>
                      <tr>
                        <th scope="col" className="triffskills-grid-corner">
                          <span className="triffskills-grid-corner-inner">
                            Plan
                            <small>Skills</small>
                          </span>
                        </th>
                        <th scope="col" className="triffskills-grid-total-head">
                          <button
                            type="button"
                            className={
                              planSort === "none"
                                ? "triffskills-head-button triffskills-sort-button"
                                : "triffskills-head-button triffskills-sort-button is-active"
                            }
                            title={`Sort plans by characters ready (${SORT_LABEL[planSort]})`}
                            aria-label={`Sort plans by characters ready. Currently ${SORT_LABEL[planSort]}.`}
                            onClick={() => setPlanSort(nextSort(planSort))}
                          >
                            <span>Ready {SORT_ARROW[planSort]}</span>
                          </button>
                        </th>
                        {orderedCharacters.map((character) => (
                          <th
                            scope="col"
                            key={character.characterId}
                            className={isDegraded(character) ? "triffskills-grid-head is-degraded" : "triffskills-grid-head"}
                          >
                            <button
                              type="button"
                              className={
                                selection?.kind === "character" && selection.characterId === character.characterId
                                  ? "triffskills-head-button is-selected"
                                  : "triffskills-head-button"
                              }
                              title={character.characterName}
                              aria-label={`Character ${character.characterName}${isDegraded(character) ? ", degraded" : ""}`}
                              onClick={() => select({ kind: "character", characterId: character.characterId })}
                            >
                              <span>{character.characterName}</span>
                            </button>
                          </th>
                        ))}
                      </tr>
                      {/* The totals row rides directly under the names, sticky at the
                          header's own height, so a character's overall usefulness stays
                          on screen no matter how far down the plan list you scroll. */}
                      <tr className="triffskills-totals-row">
                        <th scope="row" className="triffskills-grid-label triffskills-totals-label">
                          <button
                            type="button"
                            className={
                              characterSort === "none"
                                ? "triffskills-totals-sort"
                                : "triffskills-totals-sort is-active"
                            }
                            title={`Sort characters by plans ready (${SORT_LABEL[characterSort]})`}
                            aria-label={`Sort characters by plans ready. Currently ${SORT_LABEL[characterSort]}.`}
                            onClick={() => setCharacterSort(nextSort(characterSort))}
                          >
                            Plans ready {SORT_ARROW[characterSort]}
                          </button>
                        </th>
                        <td
                          className="triffskills-grid-total triffskills-total-corner"
                          title={`${readyTotal} of ${state.plans.length * orderedCharacters.length} pairs ready`}
                        >
                          {readyTotal}
                        </td>
                        {orderedCharacters.map((character) => (
                          <td
                            key={character.characterId}
                            className={
                              isDegraded(character)
                                ? "triffskills-total-cell is-degraded"
                                : "triffskills-total-cell"
                            }
                            title={`${character.characterName}: ${characterReady.get(character.characterId) ?? 0} of ${state.plans.length} plans ready`}
                          >
                            {characterReady.get(character.characterId) ?? 0}
                          </td>
                        ))}
                      </tr>
                    </thead>
                    <tbody ref={gridBodyRef} onKeyDown={onGridKeyDown}>
                      {orderedPlans.map((plan, rowIndex) => (
                        <tr key={plan.name}>
                          <th scope="row" className="triffskills-grid-label">
                            <button
                              type="button"
                              className={
                                selection?.kind === "plan" && selection.planName === plan.name
                                  ? "triffskills-plan-button is-selected"
                                  : "triffskills-plan-button"
                              }
                              title={plan.name}
                              aria-label={`Plan ${plan.name}, ${plan.requirementCount} skills`}
                              onClick={() => select({ kind: "plan", planName: plan.name })}
                            >
                              <strong>{plan.name}</strong>
                              <small>{plan.requirementCount}</small>
                            </button>
                          </th>
                          <td
                            className="triffskills-grid-total"
                            title={`${planReady.get(plan.name) ?? 0} of ${orderedCharacters.length} characters ready`}
                          >
                            <span>{planReady.get(plan.name) ?? 0}</span>
                            <small>/{orderedCharacters.length}</small>
                          </td>
                          {orderedCharacters.map((character, colIndex) => {
                            const entry = cells.get(matrixKey(character.characterId, plan.name)) || null;
                            const meta = metaFor(entry);
                            const stale = isDegraded(character);
                            const fill = fillFor(entry, plan.requirementCount);
                            const phrase = fill === undefined || fill === 1 ? "" : `, ${FILL_PHRASE[String(fill)]}`;
                            const selected =
                              selection?.kind === "cell" &&
                              selection.characterId === character.characterId &&
                              selection.planName === plan.name;

                            return (
                              <td key={character.characterId} className={stale ? "is-degraded" : ""}>
                                <button
                                  type="button"
                                  className={[
                                    "triffskills-glyph",
                                    meta.className,
                                    stale ? "is-stale" : "",
                                    selected ? "is-selected" : "",
                                  ]
                                    .filter(Boolean)
                                    .join(" ")}
                                  style={fillStyle(fill)}
                                  data-grid-row={rowIndex}
                                  data-grid-col={colIndex}
                                  tabIndex={rowIndex === cursorRow && colIndex === cursorCol ? 0 : -1}
                                  aria-label={`${character.characterName}, ${plan.name}: ${meta.label}${phrase}${stale ? ", stale" : ""}`}
                                  onClick={() => {
                                    // Clicking parks the cursor where the pointer left it, so
                                    // tabbing back into the grid resumes there instead of at
                                    // the top-left corner the user has already moved past.
                                    setCursor({ row: rowIndex, col: colIndex });
                                    select({ kind: "cell", characterId: character.characterId, planName: plan.name });
                                  }}
                                >
                                  <Mark />
                                </button>
                              </td>
                            );
                          })}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>

                {/* No panel at all until something is selected, rather than a panel
                    holding a sentence about being empty. On entry that sentence was
                    the widest thing in the rail and it does not shrink, so the state
                    with nothing to say was taking the most space away from the grid.
                    The prompt it carried is already in the section header. */}
                {selection ? (
                  <>
                    <div
                      className="triffskills-split-handle"
                      role="separator"
                      tabIndex={0}
                      aria-orientation={isRail ? "vertical" : "horizontal"}
                      aria-label={isRail ? "Resize the detail panel width" : "Resize the detail panel height"}
                      aria-valuenow={currentSize}
                      aria-valuemin={DETAIL_MIN}
                      aria-valuemax={DETAIL_MAX}
                      onPointerDown={startDrag}
                      onPointerMove={moveDrag}
                      onPointerUp={endDrag}
                      onPointerCancel={endDrag}
                      onKeyDown={nudgeDrag}
                    />

                    <DetailPanel
                      selection={selection}
                      character={selectedCharacter}
                      plan={selectedPlan}
                      characters={state.characters}
                      plans={state.plans}
                      cells={cells}
                      confirming={Boolean(selectedCharacter) && confirmForgetId === selectedCharacter?.characterId}
                      onAskForget={() => selectedCharacter && setConfirmForgetId(selectedCharacter.characterId)}
                      onCancelForget={() => setConfirmForgetId(0)}
                      onConfirmForget={() => selectedCharacter && confirmForget(selectedCharacter.characterId)}
                      onClear={clearSelection}
                    />
                  </>
                ) : null}
              </div>
            </>
          ) : null}
        </div>
      </section>

      {importDraft ? (
        <ImportPlanModal
          draft={importDraft}
          name={importName}
          onNameChange={changeImportName}
          collision={importCollision}
          submitError={importSubmitError}
          busy={importBusy}
          onCancel={closeImportModal}
          onConfirm={() => submitImport(false)}
          onReplace={() => submitImport(true)}
        />
      ) : null}
    </div>
  );
}

function DetailPanel({
  selection,
  character,
  plan,
  characters,
  plans,
  cells,
  confirming,
  onAskForget,
  onCancelForget,
  onConfirmForget,
  onClear,
}: {
  selection: Selection;
  character: SkillCharacter | null;
  plan: SkillPlanSummary | null;
  characters: SkillCharacter[];
  plans: SkillPlanSummary[];
  cells: Map<string, MatrixEntry>;
  confirming: boolean;
  onAskForget: () => void;
  onCancelForget: () => void;
  onConfirmForget: () => void;
  onClear: () => void;
}) {
  // The caller does not mount this without a selection. Kept as the narrowing
  // guard for the three branches below, not as a state the user ever sees.
  if (!selection) return null;

  if (selection.kind === "cell") {
    if (!character || !plan) return <StaleSelection onClear={onClear} />;
    return (
      <div className="triffskills-detail">
        <header className="triffskills-detail-head">
          <h3>
            {character.characterName} / {plan.name}
          </h3>
          <small>{plan.requirementCount} skills in this plan</small>
          <DetailDismiss onClear={onClear} />
        </header>
        <CellDetail entry={cells.get(matrixKey(character.characterId, plan.name)) || null} stale={isDegraded(character)} />
      </div>
    );
  }

  if (selection.kind === "plan") {
    if (!plan) return <StaleSelection onClear={onClear} />;
    const rows: DetailRow[] = characters.map((item) => ({
      key: String(item.characterId),
      label: item.characterName,
      entry: cells.get(matrixKey(item.characterId, plan.name)) || null,
    }));

    return (
      <div className="triffskills-detail">
        <header className="triffskills-detail-head">
          <h3>{plan.name}</h3>
          <small>
            {plan.requirementCount} skills / {characters.length} characters
          </small>
          <DetailDismiss onClear={onClear} />
        </header>
        <DetailGroups rows={rows} />
      </div>
    );
  }

  if (!character) return <StaleSelection onClear={onClear} />;
  const degraded = isDegraded(character);
  const stamp = formatUtc(character.fetchedUtc);
  const rows: DetailRow[] = plans.map((item) => ({
    key: item.name,
    label: item.name,
    entry: cells.get(matrixKey(character.characterId, item.name)) || null,
  }));

  return (
    <div className="triffskills-detail">
      <header className="triffskills-detail-head">
        <h3>{character.characterName}</h3>
        <small>{stamp ? `${degraded ? "Last good" : "Updated"} ${stamp}` : "Never fetched"}</small>
        <DetailDismiss onClear={onClear} />
      </header>

      {character.needsReauth ? (
        <span className="triffskills-flag">
          <em aria-hidden="true">!</em>
          {REAUTH_HINT}
        </span>
      ) : null}

      {/* Shown even when needsReauth is set. The hint above says what to do; this says why
          the character is in that state - expired sign-in, missing scopes, a 401, or the
          text of whatever the token refresh threw. Suppressing it on re-auth hid exactly
          the line a first-run user needs, since a misconfigured client ID reaches the UI
          only through this string. */}
      {character.error ? (
        <span className="triffskills-flag">
          <em aria-hidden="true">!</em>
          {character.error}
        </span>
      ) : null}

      <DetailGroups rows={rows} />

      {/* The 34px column header cannot hold a destructive control and its confirmation
          copy, so Forget lives here - selecting the character is what surfaces it. */}
      {confirming ? (
        <>
          <div className="triffskills-row-actions">
            <button type="button" className="danger-action" onClick={onConfirmForget}>
              Confirm forget
            </button>
            <button type="button" onClick={onCancelForget}>
              Cancel
            </button>
          </div>
          <small className="triffskills-confirm-note">
            Deletes the stored refresh token and this character&apos;s cached skills. Fleet Manager&apos;s
            credential for the same character is untouched.
          </small>
        </>
      ) : (
        <div className="triffskills-row-actions">
          <button type="button" onClick={onAskForget}>
            Forget character
          </button>
        </div>
      )}
    </div>
  );
}

function StaleSelection({ onClear }: { onClear: () => void }) {
  return (
    <div className="triffskills-detail is-empty">
      That selection no longer exists. Pick another cell.
      <DetailDismiss onClear={onClear} />
    </div>
  );
}

// A real button with a text label rather than a bare glyph: the toggle-to-clear
// behaviour above is not discoverable on its own, and a wordless x is not much
// of an improvement on that for anyone reading the panel with a screen reader.
function DetailDismiss({ onClear }: { onClear: () => void }) {
  return (
    <button type="button" className="triffskills-detail-dismiss" onClick={onClear}>
      Clear selection
    </button>
  );
}

function DetailGroups({ rows }: { rows: DetailRow[] }) {
  const groups = groupRows(rows);
  if (!groups.length) return null;

  return (
    <div className="triffskills-groups">
      {groups.map((group) => (
        <section key={group.key} className={`triffskills-group ${group.meta.className}`}>
          <h4>
            <Mark />
            {group.meta.label}
            <span>{group.rows.length}</span>
          </h4>
          <ul>
            {group.rows.map((row) => {
              const preview = previewMissing(row.entry);
              const summary = summaryLine(row.entry);
              return (
                <li key={row.key}>
                  <strong>{row.label}</strong>
                  {summary ? <small>{summary}</small> : null}
                  {preview ? <small className="triffskills-group-preview">{preview}</small> : null}
                </li>
              );
            })}
          </ul>
        </section>
      ))}
    </div>
  );
}

function CellDetail({ entry, stale }: { entry: MatrixEntry | null; stale: boolean }) {
  if (!entry) {
    return (
      <div className="triffskills-cell is-unscored">
        <span className="triffskills-state">
          <Mark />
          {UNSCORED_META.label}
        </span>
        <small>No result for this character and plan yet. Use Refresh characters.</small>
      </div>
    );
  }

  const meta = metaFor(entry);
  const bucket = bucketOf(entry);
  const missing = entry.missingSkills || [];
  const unknown = entry.unknownSkills || [];
  const eta = formatUtc(entry.estimatedFinishUtc);

  return (
    <div className={`triffskills-cell ${meta.className}`}>
      <span className="triffskills-state">
        <Mark />
        {meta.label}
      </span>

      {stale ? <small className="triffskills-stale">Stale - last good data</small> : null}

      {bucket === "Training" ? <small>{eta ? `Done ${eta}` : "Training, ETA unknown (queue paused)"}</small> : null}

      {bucket === "Missing" && missing.length ? (
        <ul className="triffskills-skill-list">
          {missing.map((skill) => (
            <li key={`${skill.skillName}-${skill.level}`}>
              {skill.skillName} {levelLabel(skill.level)}
            </li>
          ))}
        </ul>
      ) : null}

      {unknown.length ? (
        <div className="triffskills-unknown">
          <span>Unresolved skill names - plan cannot be fully evaluated</span>
          <ul className="triffskills-skill-list">
            {unknown.map((name) => (
              <li key={name}>{name}</li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

// Matches the established modal pattern in EveSettings.tsx:
// .triffview-modal-backdrop + .triffview-hotkey-modal, a header (h3 + p + Close), a
// body with data-hud-scroll, and a footer with Cancel and a primary-action.
function ImportPlanModal({
  draft,
  name,
  onNameChange,
  collision,
  submitError,
  busy,
  onCancel,
  onConfirm,
  onReplace,
}: {
  draft: { contents: string; preview: ImportPreview };
  name: string;
  onNameChange: (value: string) => void;
  collision: boolean;
  submitError: string;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
  onReplace: () => void;
}) {
  const trimmedName = name.trim();
  // Hinted on the trimmed value, same as submit: hinting on the raw field
  // would grey out Import over a trailing space the controller strips anyway.
  const hint = planNameHint(trimmedName);
  const remaining = draft.preview.count - draft.preview.lines.length;

  return (
    <div className="triffview-modal-backdrop">
      <section
        className="triffview-hotkey-modal triffskills-import-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="triffskills-import-title"
      >
        <header>
          <div>
            <h3 id="triffskills-import-title">Import plan from clipboard</h3>
            <p>
              {draft.preview.count === 1 ? "1 skill parsed." : `${draft.preview.count} skills parsed.`} Name the plan
              to save it.
            </p>
          </div>
          <button type="button" onClick={onCancel}>
            Close
          </button>
        </header>

        <div className="triffskills-import-body" data-hud-scroll>
          <label className="triffskills-import-name">
            <span>Plan name</span>
            <input
              autoFocus
              value={name}
              placeholder="e.g. Marauder V"
              onChange={(event) => onNameChange(event.target.value)}
            />
          </label>
          {hint ? <small className="triffskills-import-hint">{hint}</small> : null}

          <div className="triffskills-import-preview">
            <strong>Preview</strong>
            <ul className="triffskills-skill-list">
              {draft.preview.lines.map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ul>
            {remaining > 0 ? <small>...and {remaining} more</small> : null}
          </div>

          {collision ? (
            <div className="triffview-warning">
              <strong>&quot;{trimmedName}&quot; already exists.</strong>
              <span>Replace it, or Cancel and pick a different name.</span>
            </div>
          ) : null}

          {submitError ? (
            <div className="triffview-warning">
              <span>{submitError}</span>
            </div>
          ) : null}
        </div>

        <footer className="triffskills-import-actions">
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          {collision ? (
            <button
              type="button"
              className="danger-action"
              onClick={onReplace}
              disabled={busy || !trimmedName || Boolean(hint)}
            >
              Replace
            </button>
          ) : (
            <button type="button" className="primary-action" onClick={onConfirm} disabled={busy || !trimmedName || Boolean(hint)}>
              {busy ? "Importing..." : "Import"}
            </button>
          )}
        </footer>
      </section>
    </div>
  );
}
