import React, { useEffect, useMemo, useRef, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";
import "./TriffSkills.css";

type Readiness = "Ready" | "Training" | "Locked" | "Missing" | "Unknown" | "Unscored";
type RequirementState = "Active" | "TrainedInactive" | "Queued" | "Missing" | "Unknown";

type Character = {
  characterId: number;
  characterName: string;
  fetchedUtc?: string | null;
  error?: string;
  needsReauth?: boolean;
  stale?: boolean;
};

type Plan = { name: string; requirementCount: number };

type MatrixCell = {
  characterId: number;
  planName: string;
  readiness: Readiness;
  estimatedFinishUtc?: string | null;
  queueTimingUnknown?: boolean;
  activeCount: number;
  trainedInactiveCount: number;
  queuedCount: number;
  missingCount: number;
  unknownCount: number;
};

type Diagnostic = { line?: number; message: string };
type PlanIssue = { fileName: string; message: string; diagnostics?: Diagnostic[] };

type SkillsState = {
  authConfigured: boolean;
  authInProgress: boolean;
  refreshInFlight: boolean;
  characters: Character[];
  plans: Plan[];
  matrix: MatrixCell[];
  planIssues: PlanIssue[];
  warnings: string[];
  plansUpdatedUtc?: string;
};

type RequirementDetail = {
  skillName: string;
  requiredLevel: number;
  activeLevel: number | null;
  trainedLevel: number | null;
  state: RequirementState;
  queuedFinishUtc?: string | null;
  queueTimingUnknown?: boolean;
};

type CellDetail = {
  characterId: number;
  planName: string;
  readiness: Readiness;
  estimatedFinishUtc?: string | null;
  queueTimingUnknown?: boolean;
  requirements: RequirementDetail[];
};

type Preview = {
  requestId: string;
  revision: number;
  ok: boolean;
  name: string;
  requirementCount: number;
  requirements: Array<{ skillName: string; level: number }>;
  diagnostics: Diagnostic[];
  collision?: boolean;
  message?: string;
};

type Selection =
  | { kind: "cell"; characterId: number; planName: string }
  | { kind: "character"; characterId: number }
  | { kind: "plan"; planName: string };

const EMPTY_STATE: SkillsState = {
  authConfigured: false,
  authInProgress: false,
  refreshInFlight: false,
  characters: [],
  plans: [],
  matrix: [],
  planIssues: [],
  warnings: [],
};

const READINESS_ORDER: Readiness[] = ["Ready", "Training", "Locked", "Missing", "Unknown", "Unscored"];
const REQUIREMENT_ORDER: RequirementState[] = ["Active", "TrainedInactive", "Queued", "Missing", "Unknown"];
const STATUS: Record<Readiness, { label: string; description: string; sampleFill?: number }> = {
  Ready: { label: "Ready", description: "All requirements are active", sampleFill: 1 },
  Training: { label: "Training", description: "A requirement is in the queue", sampleFill: 0.5 },
  Locked: { label: "Locked", description: "Trained requirements are not active", sampleFill: 1 },
  Missing: { label: "Missing", description: "Requirements still need training", sampleFill: 0 },
  Unknown: { label: "Unknown", description: "A skill could not be resolved" },
  Unscored: { label: "Unscored", description: "No successful character fetch yet" },
};
const REQUIREMENT_LABEL: Record<RequirementState, string> = {
  Active: "Active",
  TrainedInactive: "Trained, inactive",
  Queued: "Queued",
  Missing: "Missing",
  Unknown: "Unknown",
};
const LEVELS = ["", "I", "II", "III", "IV", "V"];

const requestId = () =>
  typeof crypto?.randomUUID === "function"
    ? crypto.randomUUID().replaceAll("-", "")
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;

function send(type: string, payload: Record<string, unknown> = {}) {
  return postNative({ type, ...payload });
}

function key(characterId: number, planName: string) {
  return `${characterId}\u0000${planName}`;
}

function formatDate(value?: string | null) {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

function isDegraded(character?: Character) {
  return Boolean(character?.stale || character?.error);
}

function trainedProgress(cell: MatrixCell | undefined, requirementCount: number) {
  if (!cell || cell.readiness === "Unscored" || cell.readiness === "Unknown" || requirementCount <= 0) return undefined;
  if (cell.readiness === "Ready") return 1;
  return Math.max(0, Math.min(1, (cell.activeCount + cell.trainedInactiveCount) / requirementCount));
}

function quantizedProgress(progress?: number) {
  return progress === undefined ? undefined : Math.round(progress * 4) / 4;
}

function progressPercent(cell: MatrixCell | undefined, requirementCount: number) {
  const progress = trainedProgress(cell, requirementCount);
  return progress === undefined ? undefined : Math.round(progress * 100);
}

function statusClass(readiness: Readiness) {
  return `is-${readiness.toLowerCase()}`;
}

function selectionEquals(left: Selection | null, right: Selection) {
  if (!left || left.kind !== right.kind) return false;
  if (left.kind === "cell" && right.kind === "cell") {
    return left.characterId === right.characterId && left.planName === right.planName;
  }
  if (left.kind === "character" && right.kind === "character") return left.characterId === right.characterId;
  return left.kind === "plan" && right.kind === "plan" && left.planName === right.planName;
}

function describeCell(cell: MatrixCell | undefined, plan: Plan, character: Character) {
  const readiness = cell?.readiness ?? "Unscored";
  const percent = progressPercent(cell, plan.requirementCount);
  const pieces = [
    `${plan.name}, ${character.characterName}: ${STATUS[readiness].label}`,
    percent === undefined ? "Trained progress unavailable" : `Approximately ${percent}% trained`,
    `${cell?.activeCount ?? 0} active`,
    `${cell?.trainedInactiveCount ?? 0} trained but inactive`,
    `${cell?.queuedCount ?? 0} queued`,
    `${cell?.missingCount ?? 0} missing`,
    `${cell?.unknownCount ?? 0} unknown`,
  ];
  if (cell?.estimatedFinishUtc) pieces.push(`ETA ${formatDate(cell.estimatedFinishUtc)}`);
  if (cell?.queueTimingUnknown) pieces.push("Queue timing is unavailable or paused");
  if (isDegraded(character)) pieces.push("Stale last-good character data");
  return pieces.join("; ");
}

function ProgressMark({ readiness, fill }: { readiness: Readiness; fill?: number }) {
  const quantized = quantizedProgress(readiness === "Ready" ? 1 : fill);
  return (
    <span
      className={`triffskills-progress-mark ${statusClass(readiness)}`}
      style={{ "--tv-fill": quantized ?? 0 } as React.CSSProperties}
      aria-hidden="true"
    />
  );
}

function ImportPlanModal({
  planName,
  planText,
  preview,
  previewRequest,
  commitRequest,
  importError,
  onNameChange,
  onTextChange,
  onPreview,
  onCommit,
  onClose,
}: {
  planName: string;
  planText: string;
  preview: Preview | null;
  previewRequest: string;
  commitRequest: string;
  importError: string;
  onNameChange: (value: string) => void;
  onTextChange: (value: string) => void;
  onPreview: () => void;
  onCommit: (replace: boolean) => void;
  onClose: () => void;
}) {
  const saving = Boolean(commitRequest);
  const validating = Boolean(previewRequest);
  const canPreview = Boolean(planName.trim() && planText.trim()) && !validating && !saving;
  const canCommit = Boolean(preview?.ok && preview.revision >= 0) && !validating && !saving;

  return (
    <div className="triffview-modal-backdrop triffskills-modal-backdrop">
      <section className="triffview-hotkey-modal triffskills-import-modal" role="dialog" aria-modal="true" aria-labelledby="triffskills-import-title">
        <header>
          <div>
            <h3 id="triffskills-import-title">Import local plan</h3>
            <p>Native validation is authoritative. Nothing is saved until the validated preview is committed.</p>
          </div>
          <button type="button" disabled={saving} onClick={onClose}>Close</button>
        </header>

        <div className="triffskills-import-body" data-hud-scroll>
          <label>
            <span>Plan name</span>
            <input
              autoFocus
              maxLength={128}
              disabled={saving}
              value={planName}
              onChange={(event) => onNameChange(event.target.value)}
            />
          </label>
          <label>
            <span>Plan text</span>
            <textarea
              rows={9}
              maxLength={524288}
              disabled={saving}
              value={planText}
              placeholder="Navigation V\nSpaceship Command IV"
              onChange={(event) => onTextChange(event.target.value)}
            />
          </label>
          <small>Paste one skill per line followed by level I–V or 1–5.</small>

          {importError ? <div className="triffskills-import-error" role="alert">{importError}</div> : null}

          {preview ? (
            <section className={`triffskills-preview${preview.ok ? " is-valid" : " is-invalid"}`} aria-live="polite">
              {preview.ok ? (
                <>
                  <strong>{preview.requirementCount} validated requirement{preview.requirementCount === 1 ? "" : "s"}</strong>
                  <ul data-hud-scroll>
                    {preview.requirements.map((requirement) => (
                      <li key={requirement.skillName}>
                        <span>{requirement.skillName}</span>
                        <b>{LEVELS[requirement.level] || requirement.level}</b>
                      </li>
                    ))}
                  </ul>
                  {preview.requirementCount > preview.requirements.length ? <small>First {preview.requirements.length} shown.</small> : null}
                  {preview.collision ? (
                    <div className="triffskills-collision" role="alert">
                      A plan named <strong>{preview.name || planName.trim()}</strong> already exists. Replacing it overwrites that local file.
                    </div>
                  ) : null}
                </>
              ) : (
                <>
                  <strong>Plan validation found issues</strong>
                  <ul data-hud-scroll>
                    {(preview.diagnostics || []).map((diagnostic, index) => (
                      <li key={index}>{diagnostic.line ? `Line ${diagnostic.line}: ` : ""}{diagnostic.message}</li>
                    ))}
                  </ul>
                </>
              )}
            </section>
          ) : null}
        </div>

        <footer className="triffskills-import-actions">
          <button type="button" disabled={saving} onClick={onClose}>Cancel</button>
          <button type="button" disabled={!canPreview} onClick={onPreview}>
            {validating ? "Validating…" : "Preview"}
          </button>
          {preview?.ok ? (
            <button
              type="button"
              disabled={!canCommit}
              className={preview.collision ? "danger-action" : "primary-action"}
              onClick={() => onCommit(Boolean(preview.collision))}
            >
              {saving ? "Saving…" : preview.collision ? "Replace local plan" : "Import local plan"}
            </button>
          ) : null}
        </footer>
      </section>
    </div>
  );
}

function DetailPanel({
  selection,
  characters,
  plans,
  cells,
  detail,
  detailRequest,
  confirmForgetId,
  onSelect,
  onClear,
  onAskForget,
  onCancelForget,
  onConfirmForget,
}: {
  selection: Selection | null;
  characters: Character[];
  plans: Plan[];
  cells: Map<string, MatrixCell>;
  detail: CellDetail | null;
  detailRequest: string;
  confirmForgetId: number | null;
  onSelect: (selection: Selection) => void;
  onClear: () => void;
  onAskForget: (characterId: number) => void;
  onCancelForget: () => void;
  onConfirmForget: (characterId: number) => void;
}) {
  if (!selection) {
    return (
      <aside className="triffskills-detail-pane is-empty" data-hud-scroll aria-label="Selected detail">
        <div>
          <h3>Selected detail</h3>
          <p>Select a progress mark, character header, or plan name to inspect it here.</p>
        </div>
      </aside>
    );
  }

  const header = (title: string, subtitle: string) => (
    <header className="triffskills-detail-head">
      <div><h3>{title}</h3><p>{subtitle}</p></div>
      <button type="button" className="triffskills-detail-dismiss" onClick={onClear}>Clear</button>
    </header>
  );

  if (selection.kind === "character") {
    const character = characters.find((item) => item.characterId === selection.characterId);
    if (!character) return <aside className="triffskills-detail-pane" data-hud-scroll>{header("Selection unavailable", "The character no longer exists.")}</aside>;

    const grouped = READINESS_ORDER.map((readiness) => ({
      readiness,
      items: plans.filter((plan) => (cells.get(key(character.characterId, plan.name))?.readiness ?? "Unscored") === readiness),
    }));

    return (
      <aside className="triffskills-detail-pane" data-hud-scroll aria-live="polite">
        {header(character.characterName, "Character overview")}
        <div className="triffskills-detail-meta">
          <span>Last successful fetch</span>
          <strong>{formatDate(character.fetchedUtc) || "Never fetched"}</strong>
        </div>
        {isDegraded(character) ? <div className="triffskills-detail-flag">Stale last-good data is shown for this character.</div> : null}
        {character.error ? <div className="triffskills-detail-flag is-error">{character.error}</div> : null}
        {character.needsReauth ? <div className="triffskills-detail-flag is-error">EVE sign-in must be refreshed before this character can update.</div> : null}

        <div className="triffskills-detail-actions">
          {character.needsReauth ? (
            <button type="button" onClick={() => send("triffskills:auth")}>Re-authenticate</button>
          ) : null}
          {confirmForgetId === character.characterId ? (
            <div className="triffskills-forget-confirm" role="alert">
              <span>Delete this character’s stored TriffSkills token?</span>
              <div>
                <button type="button" onClick={onCancelForget}>Keep character</button>
                <button type="button" className="danger-action" onClick={() => onConfirmForget(character.characterId)}>Forget character</button>
              </div>
            </div>
          ) : (
            <button type="button" className="danger-action" onClick={() => onAskForget(character.characterId)}>Forget character</button>
          )}
        </div>

        <div className="triffskills-detail-groups">
          {grouped.map(({ readiness, items }) => (
            <section className={statusClass(readiness)} key={readiness}>
              <h4><ProgressMark readiness={readiness} fill={STATUS[readiness].sampleFill} />{STATUS[readiness].label}<span>{items.length}</span></h4>
              {items.length ? (
                <ul>
                  {items.map((plan) => (
                    <li key={plan.name}>
                      <button type="button" title={`Inspect ${plan.name} for ${character.characterName}`} onClick={() => onSelect({ kind: "cell", characterId: character.characterId, planName: plan.name })}>
                        <span>{plan.name}</span><small>{plan.requirementCount} req.</small>
                      </button>
                    </li>
                  ))}
                </ul>
              ) : null}
            </section>
          ))}
        </div>
      </aside>
    );
  }

  if (selection.kind === "plan") {
    const plan = plans.find((item) => item.name === selection.planName);
    if (!plan) return <aside className="triffskills-detail-pane" data-hud-scroll>{header("Selection unavailable", "The plan no longer exists.")}</aside>;

    const grouped = READINESS_ORDER.map((readiness) => ({
      readiness,
      items: characters.filter((character) => (cells.get(key(character.characterId, plan.name))?.readiness ?? "Unscored") === readiness),
    }));

    return (
      <aside className="triffskills-detail-pane" data-hud-scroll aria-live="polite">
        {header(plan.name, `${plan.requirementCount} requirement${plan.requirementCount === 1 ? "" : "s"}`)}
        <div className="triffskills-detail-groups">
          {grouped.map(({ readiness, items }) => (
            <section className={statusClass(readiness)} key={readiness}>
              <h4><ProgressMark readiness={readiness} fill={STATUS[readiness].sampleFill} />{STATUS[readiness].label}<span>{items.length}</span></h4>
              {items.length ? (
                <ul>
                  {items.map((character) => (
                    <li key={character.characterId}>
                      <button type="button" title={`Inspect ${plan.name} for ${character.characterName}`} onClick={() => onSelect({ kind: "cell", characterId: character.characterId, planName: plan.name })}>
                        <span>{character.characterName}</span><small>{isDegraded(character) ? "Stale" : "Current"}</small>
                      </button>
                    </li>
                  ))}
                </ul>
              ) : null}
            </section>
          ))}
        </div>
      </aside>
    );
  }

  const character = characters.find((item) => item.characterId === selection.characterId);
  const plan = plans.find((item) => item.name === selection.planName);
  const compact = character && plan ? cells.get(key(character.characterId, plan.name)) : undefined;
  if (!character || !plan) return <aside className="triffskills-detail-pane" data-hud-scroll>{header("Selection unavailable", "The character or plan no longer exists.")}</aside>;

  const readiness = detail?.readiness ?? compact?.readiness ?? "Unscored";
  const fill = trainedProgress(compact, plan.requirementCount);
  const percent = progressPercent(compact, plan.requirementCount);
  const groups = REQUIREMENT_ORDER.map((state) => ({
    state,
    items: detail?.requirements.filter((requirement) => requirement.state === state) ?? [],
  }));

  return (
    <aside className="triffskills-detail-pane" data-hud-scroll aria-live="polite">
      {header(plan.name, character.characterName)}
      <div className={`triffskills-selected-status ${statusClass(readiness)}`}>
        <ProgressMark readiness={readiness} fill={fill} />
        <div>
          <strong>{STATUS[readiness].label}</strong>
          <small>{percent === undefined ? "Trained progress unavailable" : `Approximately ${percent}% of requirements trained`}</small>
        </div>
      </div>
      {isDegraded(character) ? <div className="triffskills-detail-flag">Stale last-good data is shown.</div> : null}
      {compact ? (
        <div className="triffskills-counts" aria-label="Compact requirement counts">
          <span><b>{compact.activeCount}</b>Active</span>
          <span><b>{compact.trainedInactiveCount}</b>Inactive</span>
          <span><b>{compact.queuedCount}</b>Queued</span>
          <span><b>{compact.missingCount}</b>Missing</span>
          <span><b>{compact.unknownCount}</b>Unknown</span>
        </div>
      ) : null}
      {detail?.estimatedFinishUtc ? <div className="triffskills-detail-meta"><span>Estimated queue finish</span><strong>{formatDate(detail.estimatedFinishUtc)}</strong></div> : null}
      {detail?.queueTimingUnknown ? <div className="triffskills-detail-flag">Queue timing is unavailable or paused.</div> : null}

      {!detail ? <p className="triffskills-detail-loading">{detailRequest ? "Loading requirement details…" : "No requirement detail is available."}</p> : null}
      {detail ? (
        <div className="triffskills-requirement-groups">
          {groups.filter((group) => group.items.length).map(({ state, items }) => (
            <section className={`is-${state.toLowerCase()}`} key={state}>
              <h4>{REQUIREMENT_LABEL[state]}<span>{items.length}</span></h4>
              <ul>
                {items.map((requirement) => (
                  <li key={requirement.skillName}>
                    <div><strong>{requirement.skillName}</strong><small>Required {LEVELS[requirement.requiredLevel] || requirement.requiredLevel}</small></div>
                    <div className="triffskills-levels">
                      <span>Active {requirement.activeLevel ?? 0}</span>
                      <span>Trained {requirement.trainedLevel ?? 0}</span>
                    </div>
                    {requirement.queuedFinishUtc ? <small>Queue finish {formatDate(requirement.queuedFinishUtc)}</small> : null}
                    {requirement.queueTimingUnknown ? <small>Queue timing unavailable</small> : null}
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      ) : null}
    </aside>
  );
}

export default function TriffSkills() {
  const [state, setState] = useState<SkillsState>(EMPTY_STATE);
  const [error, setError] = useState("");
  const [progress, setProgress] = useState("");
  const [selection, setSelection] = useState<Selection | null>(null);
  const [detailRequest, setDetailRequest] = useState("");
  const [detail, setDetail] = useState<CellDetail | null>(null);
  const [importOpen, setImportOpen] = useState(false);
  const [importError, setImportError] = useState("");
  const [planName, setPlanName] = useState("");
  const [planText, setPlanText] = useState("");
  const [previewRequest, setPreviewRequest] = useState("");
  const [commitRequest, setCommitRequest] = useState("");
  const [preview, setPreview] = useState<Preview | null>(null);
  const [confirmForgetId, setConfirmForgetId] = useState<number | null>(null);
  const [draggedCharacterId, setDraggedCharacterId] = useState<number | null>(null);
  const [dragTargetCharacterId, setDragTargetCharacterId] = useState<number | null>(null);
  const detailRequestRef = useRef("");
  const inputRevisionRef = useRef(0);
  const previewRequestRef = useRef<{ requestId: string; revision: number } | null>(null);
  const commitRequestRef = useRef("");
  const previewRef = useRef<Preview | null>(null);
  const draggedCharacterRef = useRef<number | null>(null);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message: any) => {
      if (message?.type === "triffskills:state") {
        setState({ ...EMPTY_STATE, ...message });
        return;
      }
      if (message?.type === "triffskills:refresh-progress") {
        setProgress(`Refreshed ${message.completed ?? 0} of ${message.total ?? 0} characters`);
        return;
      }
      if (message?.type === "triffskills:error") {
        setError(`${message.action || "Skill Planner"}: ${message.message || "Unknown error"}`);
        return;
      }
      if (message?.type === "triffskills:cell-detail" && message.requestId === detailRequestRef.current) {
        detailRequestRef.current = "";
        setDetailRequest("");
        if (message.ok) setDetail(message as CellDetail);
        else setError(message.message || "Could not load cell detail.");
        return;
      }
      const pendingPreview = previewRequestRef.current;
      if (message?.type === "triffskills:plan-preview"
        && pendingPreview
        && message.requestId === pendingPreview.requestId
        && message.revision === pendingPreview.revision
        && message.revision === inputRevisionRef.current) {
        previewRequestRef.current = null;
        setPreviewRequest("");
        previewRef.current = message as Preview;
        setPreview(previewRef.current);
        setImportError("");
        return;
      }
      if (message?.type === "triffskills:plan-commit"
        && message.requestId === commitRequestRef.current
        && message.requestId === previewRef.current?.requestId
        && message.revision === previewRef.current?.revision
        && message.revision === inputRevisionRef.current) {
        commitRequestRef.current = "";
        setCommitRequest("");
        if (message.ok) {
          setPlanName("");
          setPlanText("");
          previewRef.current = null;
          setPreview(null);
          setImportError("");
          setImportOpen(false);
          setError("");
        } else if (message.collision) {
          setPreview((current) => {
            previewRef.current = current ? { ...current, collision: true } : current;
            return previewRef.current;
          });
          setImportError("");
        } else {
          setImportError(message.message || "Plan import failed.");
        }
      }
    });
    send("triffskills:get-state");
    return unsubscribe;
  }, []);

  useEffect(() => {
    if (!selection || selection.kind !== "cell") {
      setDetail(null);
      detailRequestRef.current = "";
      setDetailRequest("");
      return;
    }
    const id = requestId();
    setDetail(null);
    detailRequestRef.current = id;
    setDetailRequest(id);
    send("triffskills:get-cell-detail", { requestId: id, characterId: selection.characterId, planName: selection.planName });
  }, [selection]);

  useEffect(() => {
    if (!state.refreshInFlight) setProgress("");
  }, [state.refreshInFlight]);

  const cells = useMemo(() => {
    const result = new Map<string, MatrixCell>();
    for (const cell of state.matrix) result.set(key(cell.characterId, cell.planName), cell);
    return result;
  }, [state.matrix]);

  const totals = useMemo(() => {
    const characterReady = new Map<number, number>();
    const planReady = new Map<string, number>();
    let readyTotal = 0;
    for (const character of state.characters) {
      let ready = 0;
      for (const plan of state.plans) {
        if (cells.get(key(character.characterId, plan.name))?.readiness === "Ready") {
          ready += 1;
          readyTotal += 1;
          planReady.set(plan.name, (planReady.get(plan.name) ?? 0) + 1);
        }
      }
      characterReady.set(character.characterId, ready);
    }
    return { characterReady, planReady, readyTotal };
  }, [cells, state.characters, state.plans]);

  function choose(next: Selection) {
    setConfirmForgetId(null);
    setSelection((current) => selectionEquals(current, next) ? null : next);
  }

  function reorderCharacters(sourceId: number, targetId: number) {
    if (sourceId === targetId) return;
    const sourceIndex = state.characters.findIndex((character) => character.characterId === sourceId);
    const targetIndex = state.characters.findIndex((character) => character.characterId === targetId);
    if (sourceIndex < 0 || targetIndex < 0) return;
    const characters = [...state.characters];
    const [moved] = characters.splice(sourceIndex, 1);
    characters.splice(targetIndex, 0, moved);
    setState((current) => ({ ...current, characters }));
    send("triffskills:reorder-characters", { characterIds: characters.map((character) => character.characterId) });
  }

  function invalidatePlanPreview() {
    if (commitRequestRef.current) return;
    inputRevisionRef.current += 1;
    previewRequestRef.current = null;
    previewRef.current = null;
    setPreviewRequest("");
    setPreview(null);
    setImportError("");
  }

  function previewPlan() {
    setImportError("");
    previewRef.current = null;
    setPreview(null);
    const id = requestId();
    const revision = inputRevisionRef.current;
    previewRequestRef.current = { requestId: id, revision };
    setPreviewRequest(id);
    if (!send("triffskills:preview-plan", { requestId: id, revision, name: planName, contents: planText })) {
      previewRequestRef.current = null;
      setPreviewRequest("");
      setImportError("The native TriffView bridge is unavailable.");
    }
  }

  function commitPlan(replace: boolean) {
    const current = previewRef.current;
    if (!current?.ok || current.revision !== inputRevisionRef.current || commitRequestRef.current) return;
    commitRequestRef.current = current.requestId;
    setCommitRequest(current.requestId);
    setImportError("");
    if (!send("triffskills:commit-plan", { requestId: current.requestId, revision: current.revision, replace })) {
      commitRequestRef.current = "";
      setCommitRequest("");
      setImportError("The native TriffView bridge is unavailable.");
    }
  }

  function closeImportModal() {
    if (commitRequestRef.current) return;
    setImportOpen(false);
    setPlanName("");
    setPlanText("");
    invalidatePlanPreview();
  }

  function forgetCharacter(characterId: number) {
    send("triffskills:forget-character", { characterId });
    setConfirmForgetId(null);
    setSelection(null);
  }

  const hasNotices = Boolean(error || progress || state.warnings.length || state.planIssues.length);
  const plansStamp = state.plansUpdatedUtc ? `Plans updated ${formatDate(state.plansUpdatedUtc)}` : "No plans loaded";

  return (
    <div className="triffview-settings triffskills" data-hud-select-text-controls="true">
      <section className="triffview-settings-shell">
        <aside className="triffview-side-nav triffskills-rail">
          <div className="triffview-nav-brand">
            <h2>TriffSkills</h2>
            <p>{state.characters.length} character{state.characters.length === 1 ? "" : "s"} / {state.plans.length} plan{state.plans.length === 1 ? "" : "s"}</p>
          </div>

          <nav className="triffskills-rail-actions" aria-label="Skill Planner actions">
            {state.authInProgress ? (
              <button type="button" onClick={() => send("triffskills:cancel-auth")}>Cancel sign-in</button>
            ) : (
              <button type="button" className="primary-action" onClick={() => send("triffskills:auth")}>Add character</button>
            )}
            <button type="button" disabled={state.refreshInFlight || !state.characters.length} onClick={() => send("triffskills:refresh-characters")}>
              {state.refreshInFlight ? "Refreshing…" : "Refresh characters"}
            </button>
            <button type="button" onClick={() => send("triffskills:open-plans-folder")}>Open plans folder</button>
            <button type="button" onClick={() => send("triffskills:refresh-plans")}>Reload plans</button>
            <button type="button" onClick={() => { setImportError(""); setImportOpen(true); }}>Import local plan</button>
          </nav>

          {state.authInProgress ? <p className="triffskills-rail-status" aria-live="polite">Waiting for EVE SSO…</p> : null}
          {!state.authConfigured ? (
            <div className="triffview-warning triffskills-sso-warning">
              <strong>SSO not configured</strong>
              <span>This build needs an EVE SSO client ID before authentication can finish.</span>
            </div>
          ) : null}

          <section className="triffskills-legend" aria-label="Readiness legend">
            <h3>Readiness</h3>
            {READINESS_ORDER.map((readiness) => (
              <span className={statusClass(readiness)} key={readiness} title={STATUS[readiness].description}>
                <ProgressMark readiness={readiness} fill={STATUS[readiness].sampleFill} />
                {STATUS[readiness].label}
              </span>
            ))}
            <p>Color shows why a plan is blocked. Fill shows the share of requirements already trained.</p>
          </section>
        </aside>

        <main className="triffview-section-content">
          <header className="triffview-section-header triffskills-header">
            <div>
              <h2>Skill plan readiness</h2>
              <p>Rows are plans, columns are characters. Select a cell, plan, or character for detail.</p>
            </div>
            <span className="triffskills-plans-stamp">{plansStamp}</span>
          </header>

          <div className={`triffskills-notices${hasNotices ? "" : " is-empty"}`} data-hud-scroll aria-live="polite">
            {error ? (
              <div className="triffskills-notice is-error" role="alert"><span>{error}</span><button type="button" onClick={() => setError("")}>Dismiss</button></div>
            ) : null}
            {progress ? <div className="triffskills-notice">{progress}</div> : null}
            {state.warnings.map((warning, index) => <div className="triffskills-notice" key={`${index}:${warning}`}>{warning}</div>)}
            {state.planIssues.length ? (
              <details className="triffskills-notice triffskills-issues">
                <summary>{state.planIssues.length} plan file issue{state.planIssues.length === 1 ? "" : "s"}</summary>
                {state.planIssues.map((issue) => (
                  <div key={`${issue.fileName}:${issue.message}`}>
                    <strong>{issue.fileName}</strong>: {issue.message}
                    {(issue.diagnostics || []).map((diagnostic, index) => (
                      <p key={index}>{diagnostic.line ? `Line ${diagnostic.line}: ` : ""}{diagnostic.message}</p>
                    ))}
                  </div>
                ))}
              </details>
            ) : null}
          </div>

          <div className="triffskills-workspace">
            <section className="triffskills-matrix-pane" aria-label="Skill plan readiness matrix">
              {!state.characters.length || !state.plans.length ? (
                <div className="triffskills-empty">
                  {!state.characters.length ? <p><strong>No characters yet.</strong> Add a character through EVE SSO to begin.</p> : null}
                  {!state.plans.length ? <p><strong>No local plans yet.</strong> Import one or add a text file to the plans folder, then reload.</p> : null}
                </div>
              ) : (
                <div className="triffskills-matrix-scroll" data-hud-scroll tabIndex={0} aria-label="Character by skill plan matrix">
                  <table className="triffskills-matrix">
                    <colgroup>
                      <col className="triffskills-plan-col" />
                      <col className="triffskills-total-col" />
                      {state.characters.map((character) => <col className="triffskills-character-col" key={character.characterId} />)}
                    </colgroup>
                    <thead>
                      <tr className="triffskills-character-head-row">
                        <th className="triffskills-plan-heading" scope="col"><span>Plan</span><small>Requirements</small></th>
                        <th className="triffskills-plan-total-heading" scope="col"><span>Ready</span></th>
                        {state.characters.map((character) => {
                          const degraded = isDegraded(character);
                          const selected = selection?.kind === "character" && selection.characterId === character.characterId;
                          return (
                            <th
                              scope="col"
                              draggable
                              className={[
                                "triffskills-character-heading",
                                degraded ? "is-degraded" : "",
                                draggedCharacterId === character.characterId ? "is-dragging" : "",
                                dragTargetCharacterId === character.characterId ? "is-drag-target" : "",
                              ].filter(Boolean).join(" ")}
                              key={character.characterId}
                              onDragStart={(event) => {
                                event.dataTransfer.effectAllowed = "move";
                                event.dataTransfer.setData("text/plain", String(character.characterId));
                                draggedCharacterRef.current = character.characterId;
                                setDraggedCharacterId(character.characterId);
                              }}
                              onDragOver={(event) => {
                                if (draggedCharacterRef.current === null || draggedCharacterRef.current === character.characterId) return;
                                event.preventDefault();
                                event.dataTransfer.dropEffect = "move";
                                setDragTargetCharacterId(character.characterId);
                              }}
                              onDrop={(event) => {
                                event.preventDefault();
                                const sourceId = draggedCharacterRef.current ?? Number(event.dataTransfer.getData("text/plain"));
                                if (Number.isSafeInteger(sourceId) && sourceId > 0) reorderCharacters(sourceId, character.characterId);
                                draggedCharacterRef.current = null;
                                setDraggedCharacterId(null);
                                setDragTargetCharacterId(null);
                              }}
                              onDragEnd={() => {
                                draggedCharacterRef.current = null;
                                setDraggedCharacterId(null);
                                setDragTargetCharacterId(null);
                              }}
                            >
                              <button
                                type="button"
                                className={selected ? "is-selected" : ""}
                                title={`${character.characterName}. Click for character details; drag to reorder.${degraded ? " Stale or degraded." : ""}`}
                                aria-label={`Character ${character.characterName}. ${totals.characterReady.get(character.characterId) ?? 0} of ${state.plans.length} plans ready.${degraded ? " Stale or degraded data." : ""}`}
                                aria-pressed={selected}
                                onClick={() => choose({ kind: "character", characterId: character.characterId })}
                              ><span>{character.characterName}</span></button>
                            </th>
                          );
                        })}
                      </tr>
                      <tr className="triffskills-totals-row">
                        <th scope="row">Plans ready</th>
                        <td title={`${totals.readyTotal} of ${state.plans.length * state.characters.length} character-plan pairs ready`}>
                          <strong>{totals.readyTotal}</strong><small>/{state.plans.length * state.characters.length}</small>
                        </td>
                        {state.characters.map((character) => (
                          <td
                            className={isDegraded(character) ? "is-degraded" : ""}
                            key={character.characterId}
                            title={`${character.characterName}: ${totals.characterReady.get(character.characterId) ?? 0} of ${state.plans.length} plans ready`}
                          >
                            <strong>{totals.characterReady.get(character.characterId) ?? 0}</strong><small>/{state.plans.length}</small>
                          </td>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {state.plans.map((plan) => {
                        const planSelected = selection?.kind === "plan" && selection.planName === plan.name;
                        return (
                          <tr key={plan.name}>
                            <th scope="row">
                              <button
                                type="button"
                                className={planSelected ? "is-selected" : ""}
                                title={`${plan.name}: ${plan.requirementCount} requirements`}
                                aria-label={`Plan ${plan.name}, ${plan.requirementCount} requirements, ${totals.planReady.get(plan.name) ?? 0} of ${state.characters.length} characters ready`}
                                aria-pressed={planSelected}
                                onClick={() => choose({ kind: "plan", planName: plan.name })}
                              ><strong>{plan.name}</strong><small>{plan.requirementCount}</small></button>
                            </th>
                            <td className="triffskills-plan-total" title={`${totals.planReady.get(plan.name) ?? 0} of ${state.characters.length} characters ready`}>
                              <strong>{totals.planReady.get(plan.name) ?? 0}</strong><small>/{state.characters.length}</small>
                            </td>
                            {state.characters.map((character) => {
                              const cell = cells.get(key(character.characterId, plan.name));
                              const readiness = cell?.readiness ?? "Unscored";
                              const active = selection?.kind === "cell" && selection.characterId === character.characterId && selection.planName === plan.name;
                              const description = describeCell(cell, plan, character);
                              return (
                                <td className={isDegraded(character) ? "is-degraded" : ""} key={character.characterId}>
                                  <button
                                    type="button"
                                    className={`triffskills-cell-button ${statusClass(readiness)}${active ? " is-selected" : ""}`}
                                    title={description}
                                    aria-label={description}
                                    aria-pressed={active}
                                    onClick={() => choose({ kind: "cell", characterId: character.characterId, planName: plan.name })}
                                  >
                                    <ProgressMark readiness={readiness} fill={trainedProgress(cell, plan.requirementCount)} />
                                  </button>
                                </td>
                              );
                            })}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </section>

            <DetailPanel
              selection={selection}
              characters={state.characters}
              plans={state.plans}
              cells={cells}
              detail={detail}
              detailRequest={detailRequest}
              confirmForgetId={confirmForgetId}
              onSelect={choose}
              onClear={() => { setSelection(null); setConfirmForgetId(null); }}
              onAskForget={setConfirmForgetId}
              onCancelForget={() => setConfirmForgetId(null)}
              onConfirmForget={forgetCharacter}
            />
          </div>
        </main>
      </section>

      {importOpen ? (
        <ImportPlanModal
          planName={planName}
          planText={planText}
          preview={preview}
          previewRequest={previewRequest}
          commitRequest={commitRequest}
          importError={importError}
          onNameChange={(value) => { setPlanName(value); invalidatePlanPreview(); }}
          onTextChange={(value) => { setPlanText(value); invalidatePlanPreview(); }}
          onPreview={previewPlan}
          onCommit={commitPlan}
          onClose={closeImportModal}
        />
      ) : null}
    </div>
  );
}
