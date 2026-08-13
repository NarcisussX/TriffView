import React, { useEffect, useMemo, useRef, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";
import "./TriffSkills.css";

type Readiness = "Ready" | "Training" | "Locked" | "Missing" | "Unknown" | "Unscored";

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
  activeLevel: number;
  trainedLevel: number;
  state: "Active" | "TrainedInactive" | "Queued" | "Missing" | "Unknown";
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

const STATUS: Record<Readiness, { symbol: string; label: string }> = {
  Ready: { symbol: "✓", label: "Ready (all requirements active)" },
  Training: { symbol: "◔", label: "Training in queue" },
  Locked: { symbol: "◐", label: "Trained but not active" },
  Missing: { symbol: "—", label: "Missing requirements" },
  Unknown: { symbol: "?", label: "Contains an unresolved skill" },
  Unscored: { symbol: "·", label: "No successful character fetch yet" },
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

function describeCell(cell: MatrixCell) {
  const pieces = [
    `${STATUS[cell.readiness]?.label ?? "Unknown status"}.`,
    `${cell.activeCount} active`,
    `${cell.trainedInactiveCount} trained inactive`,
    `${cell.queuedCount} queued`,
    `${cell.missingCount} missing`,
    `${cell.unknownCount} unknown`,
  ];
  if (cell.estimatedFinishUtc) pieces.push(`ETA ${formatDate(cell.estimatedFinishUtc)}`);
  if (cell.queueTimingUnknown) pieces.push("Queue timing is unavailable or paused");
  return pieces.join("; ");
}

export default function TriffSkills() {
  const [state, setState] = useState<SkillsState>(EMPTY_STATE);
  const [error, setError] = useState("");
  const [progress, setProgress] = useState("");
  const [selected, setSelected] = useState<{ characterId: number; planName: string } | null>(null);
  const [detailRequest, setDetailRequest] = useState("");
  const [detail, setDetail] = useState<CellDetail | null>(null);
  const [planName, setPlanName] = useState("");
  const [planText, setPlanText] = useState("");
  const [previewRequest, setPreviewRequest] = useState("");
  const [commitRequest, setCommitRequest] = useState("");
  const [preview, setPreview] = useState<Preview | null>(null);
  const detailRequestRef = useRef("");
  const inputRevisionRef = useRef(0);
  const previewRequestRef = useRef<{ requestId: string; revision: number } | null>(null);
  const commitRequestRef = useRef("");
  const previewRef = useRef<Preview | null>(null);

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
          setError("");
        } else if (message.collision) {
          setPreview((current) => {
            previewRef.current = current ? { ...current, collision: true } : current;
            return previewRef.current;
          });
        } else {
          setError(message.message || "Plan import failed.");
        }
      }
    });
    send("triffskills:get-state");
    return unsubscribe;
  }, []);

  useEffect(() => {
    if (!selected) {
      setDetail(null);
      detailRequestRef.current = "";
      setDetailRequest("");
      return;
    }
    const id = requestId();
    setDetail(null);
    detailRequestRef.current = id;
    setDetailRequest(id);
    send("triffskills:get-cell-detail", { requestId: id, ...selected });
  }, [selected]);

  useEffect(() => {
    if (!state.refreshInFlight) setProgress("");
  }, [state.refreshInFlight]);

  const cells = useMemo(() => {
    const result = new Map<string, MatrixCell>();
    for (const cell of state.matrix) result.set(key(cell.characterId, cell.planName), cell);
    return result;
  }, [state.matrix]);

  const selectedCharacter = selected
    ? state.characters.find((character) => character.characterId === selected.characterId)
    : undefined;

  function chooseCell(characterId: number, planNameValue: string) {
    setSelected((current) =>
      current?.characterId === characterId && current.planName === planNameValue
        ? null
        : { characterId, planName: planNameValue },
    );
  }

  function previewPlan() {
    setError("");
    previewRef.current = null;
    setPreview(null);
    const id = requestId();
    const revision = inputRevisionRef.current;
    previewRequestRef.current = { requestId: id, revision };
    setPreviewRequest(id);
    if (!send("triffskills:preview-plan", { requestId: id, revision, name: planName, contents: planText })) {
      previewRequestRef.current = null;
      setPreviewRequest("");
      setError("The native TriffView bridge is unavailable.");
    }
  }

  function commitPlan(replace: boolean) {
    const current = previewRef.current;
    if (!current?.ok || current.revision !== inputRevisionRef.current || commitRequestRef.current) return;
    commitRequestRef.current = current.requestId;
    setCommitRequest(current.requestId);
    if (!send("triffskills:commit-plan", { requestId: current.requestId, revision: current.revision, replace })) {
      commitRequestRef.current = "";
      setCommitRequest("");
      setError("The native TriffView bridge is unavailable.");
    }
  }

  function invalidatePlanPreview() {
    inputRevisionRef.current += 1;
    previewRequestRef.current = null;
    commitRequestRef.current = "";
    previewRef.current = null;
    setPreviewRequest("");
    setCommitRequest("");
    setPreview(null);
  }

  return (
    <section className="triffview-section triffskills">
      <div className="triffview-section-content" data-hud-scroll>
        <header className="triffview-section-header triffskills-header">
          <div>
            <h2>Skill Planner</h2>
            <p>Compare local skill plans across authenticated EVE characters.</p>
          </div>
          <div className="triffskills-actions">
            {state.authInProgress ? (
              <button type="button" onClick={() => send("triffskills:cancel-auth")}>Cancel sign-in</button>
            ) : (
              <button type="button" className="primary-action" onClick={() => send("triffskills:auth")}>Add character</button>
            )}
            <button type="button" disabled={state.refreshInFlight || !state.characters.length} onClick={() => send("triffskills:refresh-characters")}>
              {state.refreshInFlight ? "Refreshing…" : "Refresh"}
            </button>
            <button type="button" onClick={() => send("triffskills:open-plans-folder")}>Open plans folder</button>
            <button type="button" onClick={() => send("triffskills:refresh-plans")}>Reload plans</button>
          </div>
        </header>

        {!state.authConfigured ? <div className="triffview-warning">EVE SSO is not configured for this build.</div> : null}
        {error ? (
          <div className="triffskills-notice is-error" role="alert">
            <span>{error}</span><button type="button" onClick={() => setError("")}>Dismiss</button>
          </div>
        ) : null}
        {progress ? <div className="triffskills-notice" aria-live="polite">{progress}</div> : null}
        {state.warnings.map((warning) => <div className="triffskills-notice" key={warning}>{warning}</div>)}

        <div className="triffskills-workspace">
          <main className="triffskills-main">
            <div className="triffskills-legend" aria-label="Matrix legend">
              {(Object.keys(STATUS) as Readiness[]).map((status) => (
                <span className={`is-${status.toLowerCase()}`} key={status}><b>{STATUS[status].symbol}</b>{STATUS[status].label}</span>
              ))}
            </div>

            {!state.characters.length || !state.plans.length ? (
              <div className="triffskills-empty">
                {!state.characters.length ? "Add a character to begin. " : ""}
                {!state.plans.length ? "Import a local plan or add one to the plans folder." : ""}
              </div>
            ) : (
              <div className="triffskills-matrix-scroll" data-hud-scroll tabIndex={0} aria-label="Character by skill plan matrix">
                <table className="triffskills-matrix">
                  <thead>
                    <tr>
                      <th className="triffskills-plan-heading" scope="col">Plan</th>
                      {state.characters.map((character) => (
                        <th scope="col" className={character.error ? "has-warning" : ""} key={character.characterId}>
                          <span title={character.characterName}>{character.characterName}</span>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {state.plans.map((plan) => (
                      <tr key={plan.name}>
                        <th scope="row" title={`${plan.requirementCount} requirements`}>
                          <strong>{plan.name}</strong><small>{plan.requirementCount}</small>
                        </th>
                        {state.characters.map((character) => {
                          const cell = cells.get(key(character.characterId, plan.name));
                          const readiness = cell?.readiness ?? "Unscored";
                          const active = selected?.characterId === character.characterId && selected.planName === plan.name;
                          return (
                            <td key={character.characterId}>
                              <button
                                type="button"
                                className={`triffskills-cell is-${readiness.toLowerCase()}${active ? " is-selected" : ""}`}
                                title={cell ? describeCell(cell) : STATUS.Unscored.label}
                                aria-label={`${plan.name}, ${character.characterName}: ${STATUS[readiness].label}`}
                                aria-pressed={active}
                                onClick={() => chooseCell(character.characterId, plan.name)}
                              >
                                {STATUS[readiness].symbol}
                              </button>
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {state.planIssues.length ? (
              <details className="triffskills-issues">
                <summary>{state.planIssues.length} plan file issue{state.planIssues.length === 1 ? "" : "s"}</summary>
                {state.planIssues.map((issue) => (
                  <div key={`${issue.fileName}:${issue.message}`}><strong>{issue.fileName}</strong>: {issue.message}
                    {(issue.diagnostics || []).map((diagnostic, index) => (
                      <p key={index}>{diagnostic.line ? `Line ${diagnostic.line}: ` : ""}{diagnostic.message}</p>
                    ))}
                  </div>
                ))}
              </details>
            ) : null}
          </main>

          <aside className="triffskills-sidebar">
            <section className="triffskills-panel triffskills-characters">
              <h3>Characters</h3>
              <div className="triffskills-character-list" data-hud-scroll>
                {!state.characters.length ? <p>No authenticated characters.</p> : null}
                {state.characters.map((character) => (
                  <article className="triffskills-character" key={character.characterId}>
                    <div><strong>{character.characterName}</strong><small>{formatDate(character.fetchedUtc) || "Never fetched"}</small></div>
                    {character.error ? <p className="is-error">{character.error}</p> : null}
                    <div>
                      {character.needsReauth ? <button type="button" onClick={() => send("triffskills:auth")}>Re-authenticate</button> : null}
                      <button type="button" className="danger-action" onClick={() => {
                        if (window.confirm(`Forget ${character.characterName} and delete its stored TriffSkills token?`)) {
                          send("triffskills:forget-character", { characterId: character.characterId });
                          if (selected?.characterId === character.characterId) setSelected(null);
                        }
                      }}>Forget</button>
                    </div>
                  </article>
                ))}
              </div>
            </section>

            <section className="triffskills-panel triffskills-detail" aria-live="polite">
              <h3>Selected cell</h3>
              {!selected ? <p>Select a matrix cell to inspect its requirements.</p> : null}
              {selected && !detail ? <p>{detailRequest ? "Loading…" : "No detail available."}</p> : null}
              {detail ? (
                <>
                  <div className="triffskills-detail-title">
                    <strong>{detail.planName}</strong><span>{selectedCharacter?.characterName}</span>
                    <b className={`is-${detail.readiness.toLowerCase()}`}>{STATUS[detail.readiness].label}</b>
                    {detail.estimatedFinishUtc ? <small>ETA {formatDate(detail.estimatedFinishUtc)}</small> : null}
                    {detail.queueTimingUnknown ? <small>Queue timing is unavailable or paused.</small> : null}
                  </div>
                  <ul className="triffskills-requirements" data-hud-scroll>
                    {detail.requirements.map((requirement) => (
                      <li className={`is-${requirement.state.toLowerCase()}`} key={requirement.skillName}>
                        <span><strong>{requirement.skillName}</strong><small>{requirement.state}</small></span>
                        <b>{LEVELS[requirement.requiredLevel] || requirement.requiredLevel}</b>
                        <small>A{requirement.activeLevel} / T{requirement.trainedLevel}</small>
                      </li>
                    ))}
                  </ul>
                </>
              ) : null}
            </section>

            <section className="triffskills-panel triffskills-import">
              <h3>Import local plan</h3>
              <p>Paste one skill per line, followed by level I–V or 1–5. Native validation runs before anything is saved.</p>
              <label><span>Plan name</span><input maxLength={128} value={planName} onChange={(event) => { setPlanName(event.target.value); invalidatePlanPreview(); }} /></label>
              <label><span>Plan text</span><textarea rows={8} maxLength={524288} value={planText} onChange={(event) => { setPlanText(event.target.value); invalidatePlanPreview(); }} /></label>
              <button type="button" disabled={Boolean(previewRequest) || !planName || !planText} onClick={previewPlan}>
                {previewRequest ? "Validating…" : "Preview import"}
              </button>
              {preview ? (
                <div className={`triffskills-preview${preview.ok ? " is-valid" : " is-invalid"}`}>
                  {preview.ok ? (
                    <>
                      <strong>{preview.requirementCount} validated requirements</strong>
                      <ul>{preview.requirements.map((requirement) => <li key={requirement.skillName}>{requirement.skillName} {LEVELS[requirement.level] || requirement.level}</li>)}</ul>
                      {preview.requirementCount > preview.requirements.length ? <small>First {preview.requirements.length} shown.</small> : null}
                      {preview.collision ? <p>A plan with this name exists. Replacing it overwrites that local file.</p> : null}
                      <button type="button" disabled={Boolean(commitRequest) || preview.revision !== inputRevisionRef.current} className={preview.collision ? "danger-action" : "primary-action"} onClick={() => commitPlan(Boolean(preview.collision))}>
                        {commitRequest ? "Saving…" : preview.collision ? "Replace local plan" : "Import local plan"}
                      </button>
                    </>
                  ) : (
                    <ul>{preview.diagnostics.map((diagnostic, index) => <li key={index}>{diagnostic.line ? `Line ${diagnostic.line}: ` : ""}{diagnostic.message}</li>)}</ul>
                  )}
                </div>
              ) : null}
            </section>
          </aside>
        </div>
      </div>
    </section>
  );
}
