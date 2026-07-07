import React, { useEffect, useMemo, useState } from "react";
import { copyText, onNativeMessage, postNative } from "../nativeBridge.js";

type FleetBoss = {
  characterId: number;
  characterName: string;
  scopes: string[];
  authenticatedUtc: string;
  tokenStored: boolean;
};

type LiveFleet = {
  fleetId: number;
  fleetBossId: number;
  role: string;
  wingId: number;
  squadId: number;
  canModify: boolean;
  detectedUtc: string;
};

type FleetMember = {
  characterName: string;
  role: string;
};

type FleetSquad = {
  name: string;
  members: FleetMember[];
};

type FleetWing = {
  name: string;
  squads: FleetSquad[];
};

type FleetProfile = {
  version: number;
  id: string;
  name: string;
  description: string;
  keepExistingMembersInFleet: boolean;
  wings: FleetWing[];
};

type FleetStructureAction = {
  position: number;
  name: string;
  parentName?: string;
};

type FleetRenameAction = {
  id: number;
  from: string;
  to: string;
};

type FleetInvitePlan = {
  characterName: string;
  characterId: number;
  role: string;
  wingName: string;
  squadName: string;
};

type DryRunPlan = {
  generatedUtc: string;
  fleetId: number;
  profileId: string;
  profileName: string;
  canApply: boolean;
  warnings: string[];
  errors: string[];
  wingsToCreate: FleetStructureAction[];
  wingsToRename: FleetRenameAction[];
  squadsToCreate: FleetStructureAction[];
  squadsToRename: FleetRenameAction[];
  invites: FleetInvitePlan[];
  alreadyInFleet: FleetInvitePlan[];
  unresolvedCharacters: string[];
  duplicateCharacters: string[];
};

type ApplyResult = {
  kind: string;
  name: string;
  id: number;
  status: string;
  detail: string;
};

type ApplySummary = {
  appliedUtc: string;
  results: ApplyResult[];
};

type TriffFleetsState = {
  authConfigured: boolean;
  authInProgress: boolean;
  requiredScopes: string[];
  redirectUri: string;
  selectedBossCharacterId: number;
  bosses: FleetBoss[];
  liveFleet: LiveFleet | null;
  selectedProfileId: string;
  profiles: FleetProfile[];
  dryRun: DryRunPlan | null;
  applyResult: ApplySummary | null;
  complianceNote: string;
};

const FLEET_STRUCTURE_NAME_MAX_LENGTH = 10;

const EMPTY_STATE: TriffFleetsState = {
  authConfigured: false,
  authInProgress: false,
  requiredScopes: [],
  redirectUri: "",
  selectedBossCharacterId: 0,
  bosses: [],
  liveFleet: null,
  selectedProfileId: "",
  profiles: [],
  dryRun: null,
  applyResult: null,
  complianceNote: "Uses ESI only. Does not control EVE clients. Characters accept invites manually in-game.",
};

const ROLE_OPTIONS = [
  ["squad_member", "Squad member"],
  ["squad_commander", "Squad commander"],
  ["wing_commander", "Wing commander"],
  ["fleet_commander", "Fleet commander"],
] as const;

function send(type: string, payload: Record<string, unknown> = {}) {
  postNative({ type, ...payload });
}

function emptyProfile(): FleetProfile {
  return {
    version: 1,
    id: crypto.randomUUID ? crypto.randomUUID().replace(/-/g, "") : String(Date.now()),
    name: "Default Fleet",
    description: "",
    keepExistingMembersInFleet: true,
    wings: [
      {
        name: "DPS Wing",
        squads: [{ name: "Main DPS", members: [] }],
      },
    ],
  };
}

function cloneProfile(profile: FleetProfile): FleetProfile {
  return JSON.parse(JSON.stringify(profile));
}

function formatDate(value?: string) {
  if (!value) return "-";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "-";
  return date.toLocaleString(undefined, {
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function selectedOrFirst<T extends { id: string }>(items: T[], selectedId: string) {
  return items.find((item) => item.id === selectedId) || items[0] || null;
}

function statusClass(status: string) {
  if (status.includes("failed")) return "is-danger";
  if (status.includes("invited") || status.includes("created") || status.includes("renamed") || status.includes("deleted") || status.includes("moved") || status.includes("benched")) return "is-success";
  if (status.includes("already") || status.includes("skipped") || status.includes("kept")) return "is-warning";
  return "";
}

export default function TriffFleets() {
  const [state, setState] = useState<TriffFleetsState>(EMPTY_STATE);
  const [activeSection, setActiveSection] = useState("boss");
  const [draft, setDraft] = useState<FleetProfile>(emptyProfile);
  const [dirty, setDirty] = useState(false);
  const [error, setError] = useState("");

  const selectedProfile = useMemo(() => selectedOrFirst(state.profiles, state.selectedProfileId), [state.profiles, state.selectedProfileId]);
  const selectedBoss = useMemo(
    () => state.bosses.find((boss) => boss.characterId === state.selectedBossCharacterId) || state.bosses[0] || null,
    [state.bosses, state.selectedBossCharacterId]
  );

  useEffect(() => {
    const unsubscribe = onNativeMessage((message) => {
      if (message?.type === "trifffleets:state") {
        setState({
          ...EMPTY_STATE,
          ...(message as TriffFleetsState),
        });
      }
      if (message?.type === "trifffleets:error") {
        setError(String(message.message || "Fleet Manager error"));
      }
    });

    send("trifffleets:get-state");
    return unsubscribe;
  }, []);

  useEffect(() => {
    if (!selectedProfile) return;
    setDraft(cloneProfile(selectedProfile));
    setDirty(false);
  }, [selectedProfile?.id]);

  function patchDraft(mutator: (next: FleetProfile) => void) {
    setDraft((current) => {
      const next = cloneProfile(current);
      mutator(next);
      setDirty(true);
      return next;
    });
  }

  function saveDraft() {
    send("trifffleets:save-profile", { profile: draft });
    setDirty(false);
  }

  function addWing() {
    patchDraft((next) => {
      next.wings.push({ name: `Wing ${next.wings.length + 1}`, squads: [{ name: "Squad 1", members: [] }] });
    });
  }

  function removeWing(index: number) {
    patchDraft((next) => {
      if (next.wings.length <= 1) return;
      next.wings.splice(index, 1);
    });
  }

  function addSquad(wingIndex: number) {
    patchDraft((next) => {
      const wing = next.wings[wingIndex];
      wing.squads.push({ name: `Squad ${wing.squads.length + 1}`, members: [] });
    });
  }

  function removeSquad(wingIndex: number, squadIndex: number) {
    patchDraft((next) => {
      const wing = next.wings[wingIndex];
      if (wing.squads.length <= 1) return;
      wing.squads.splice(squadIndex, 1);
    });
  }

  function addMember(wingIndex: number, squadIndex: number) {
    patchDraft((next) => {
      next.wings[wingIndex].squads[squadIndex].members.push({ characterName: "", role: "squad_member" });
    });
  }

  function removeMember(wingIndex: number, squadIndex: number, memberIndex: number) {
    patchDraft((next) => {
      next.wings[wingIndex].squads[squadIndex].members.splice(memberIndex, 1);
    });
  }

  function copyLog() {
    const lines = (state.applyResult?.results || []).map((item) => `${item.kind}\t${item.name}\t${item.status}\t${item.detail}`);
    copyText(lines.join("\n"));
  }

  function selectBoss(value: string) {
    if (value === "__add__") {
      send("trifffleets:start-auth");
      return;
    }
    send("trifffleets:select-boss", { characterId: Number(value) || 0 });
  }

  function applyFleet() {
    if (dirty) saveDraft();
    send("trifffleets:apply-plan");
    setActiveSection("results");
  }

  const navItems = [
    ["boss", "Fleet boss"],
    ["profiles", "Fleet profiles"],
    ["results", `Result log${state.applyResult ? ` (${state.applyResult.results.length})` : ""}`],
  ];

  return (
    <div className="triffview-settings trifffleets" data-hud-scroll data-hud-select-text-controls="true">
      <section className="triffview-settings-shell">
        <aside className="triffview-side-nav">
          <div className="triffview-nav-brand">
            <h2>TriffFleets</h2>
            <p>{state.liveFleet ? `Fleet ${state.liveFleet.fleetId}` : "ESI fleet assistant"}</p>
          </div>
          <nav aria-label="Fleet Manager sections">
            {navItems.map(([id, label]) => (
              <button type="button" key={id} className={activeSection === id ? "is-active" : ""} onClick={() => setActiveSection(id)}>
                {label}
              </button>
            ))}
          </nav>
          <div className="triffview-nav-actions">
            <button type="button" className="primary-action" onClick={() => send("trifffleets:detect-fleet")} disabled={!selectedBoss}>
              Detect Fleet
            </button>
            <button
              type="button"
              className="primary-action"
              onClick={applyFleet}
              disabled={!state.liveFleet?.canModify || !selectedProfile}
            >
              Apply Structure + Invite
            </button>
          </div>
        </aside>

        <div className="triffview-section-content" data-hud-scroll>
          <header className="triffview-section-header eve-settings-header">
            <div>
              <h2>{activeSection === "boss" ? "Fleet boss" : activeSection === "profiles" ? "Fleet profiles" : "Result log"}</h2>
              <p>{state.complianceNote}</p>
            </div>
            <span className={state.liveFleet?.canModify ? "eve-settings-running is-running" : "eve-settings-running"}>
              {state.liveFleet ? (state.liveFleet.canModify ? "Fleet ready" : "No modify rights") : "No fleet"}
            </span>
          </header>

          {error ? (
            <div className="triffview-warning trifffleets-error">
              <strong>Fleet Manager</strong>
              <span>{error}</span>
              <button type="button" onClick={() => setError("")}>
                Clear
              </button>
            </div>
          ) : null}

          {activeSection === "boss" ? (
            <div className="trifffleets-stack">
              <div className="triffview-panel">
                <div className="trifffleets-two-col">
                  <div className="triffview-field">
                    <label>Selected boss</label>
                    <select
                      value={selectedBoss?.characterId || ""}
                      onChange={(event) => selectBoss(event.target.value)}
                      disabled={state.authInProgress}
                    >
                      {!state.bosses.length ? <option value="">No authenticated fleet boss</option> : null}
                      {state.bosses.map((boss) => (
                        <option key={boss.characterId} value={boss.characterId}>
                          {boss.characterName} ({boss.characterId})
                        </option>
                      ))}
                      <option value="__add__">{state.authInProgress ? "Waiting for EVE SSO..." : "Add new character..."}</option>
                    </select>
                  </div>
                  <div className="triffview-field">
                    <label>Auth status</label>
                    <div className="trifffleets-readonly">
                      {selectedBoss ? `${selectedBoss.tokenStored ? "Stored securely" : "Needs re-auth"} - ${formatDate(selectedBoss.authenticatedUtc)}` : "Authenticate a fleet boss"}
                    </div>
                  </div>
                </div>
                {!state.authConfigured ? (
                  <div className="triffview-warning">
                    <strong>SSO client ID missing.</strong>
                    <span>Set the built-in TriffView EVE SSO client ID before authenticating a fleet boss.</span>
                  </div>
                ) : null}
                <div className="trifffleets-actions-row">
                  <button type="button" onClick={() => send("trifffleets:detect-fleet")} disabled={!selectedBoss}>
                    Detect Fleet
                  </button>
                  <button type="button" onClick={() => selectedBoss && send("trifffleets:forget-boss", { characterId: selectedBoss.characterId })} disabled={!selectedBoss}>
                    Forget boss
                  </button>
                </div>
              </div>

              <div className="triffview-band">
                <div className="triffview-band-top">
                  <div>
                    <h2>Detected live fleet</h2>
                    <p>ESI cannot create a fleet from nothing. Make the fleet in-game first.</p>
                  </div>
                </div>
                {state.liveFleet ? (
                  <div className="trifffleets-stat-grid">
                    <div>
                      <span>Fleet ID</span>
                      <strong>{state.liveFleet.fleetId}</strong>
                    </div>
                    <div>
                      <span>Boss ID</span>
                      <strong>{state.liveFleet.fleetBossId}</strong>
                    </div>
                    <div>
                      <span>Detected role</span>
                      <strong>{state.liveFleet.role}</strong>
                    </div>
                    <div>
                      <span>Modify permission</span>
                      <strong>{state.liveFleet.canModify ? "Ready" : "Blocked"}</strong>
                    </div>
                  </div>
                ) : (
                  <div className="eve-settings-empty">No fleet detected yet. Create a fleet in-game, wait up to 60 seconds for ESI cache, then click Detect Fleet.</div>
                )}
              </div>
            </div>
          ) : null}

          {activeSection === "profiles" ? (
            <div className="trifffleets-stack">
              <div className="triffview-panel">
                <div className="trifffleets-profile-head">
                  <div className="triffview-field">
                    <label>Selected profile</label>
                    <select value={selectedProfile?.id || ""} onChange={(event) => send("trifffleets:select-profile", { profileId: event.target.value })}>
                      {state.profiles.map((profile) => (
                        <option value={profile.id} key={profile.id}>
                          {profile.name}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="trifffleets-actions-row trifffleets-profile-actions">
                    <button type="button" onClick={() => send("trifffleets:create-profile", { name: "New Fleet Profile" })}>
                      New profile
                    </button>
                    <button type="button" onClick={() => selectedProfile && send("trifffleets:delete-profile", { profileId: selectedProfile.id })} disabled={!selectedProfile || state.profiles.length <= 1}>
                      Delete
                    </button>
                    <button type="button" onClick={() => send("trifffleets:import-profile-json")}>
                      Import JSON
                    </button>
                    <button type="button" onClick={() => selectedProfile && send("trifffleets:export-profile-json", { profileId: selectedProfile.id })} disabled={!selectedProfile}>
                      Export JSON
                    </button>
                  </div>
                </div>
                <div className="trifffleets-two-col">
                  <div className="triffview-field">
                    <label>Profile name</label>
                    <input value={draft.name} onChange={(event) => patchDraft((next) => (next.name = event.target.value))} />
                  </div>
                  <div className="triffview-field">
                    <label>Description</label>
                    <input value={draft.description} onChange={(event) => patchDraft((next) => (next.description = event.target.value))} />
                  </div>
                </div>
                <label className="trifffleets-toggle-row">
                  <input
                    type="checkbox"
                    checked={draft.keepExistingMembersInFleet !== false}
                    onChange={(event) => patchDraft((next) => (next.keepExistingMembersInFleet = event.target.checked))}
                  />
                  <span>Keep existing members in fleet, even if unassigned</span>
                </label>
                <div className="trifffleets-actions-row trifffleets-editor-actions">
                  <button type="button" className="primary-action" onClick={saveDraft} disabled={!dirty}>
                    Save profile
                  </button>
                  <button type="button" onClick={addWing}>
                    Add wing
                  </button>
                </div>
              </div>

              <div className="trifffleets-tree">
                {draft.wings.map((wing, wingIndex) => (
                  <section className="triffview-band trifffleets-wing" key={`wing-${wingIndex}`}>
                    <div className="trifffleets-tree-head trifffleets-wing-head">
                      <span className="trifffleets-node-caret" aria-hidden="true" />
                      <div className="triffview-field">
                        <div className="trifffleets-field-label-row">
                          <label>Wing {wingIndex + 1}</label>
                          <span>{wing.name.trim().length}/{FLEET_STRUCTURE_NAME_MAX_LENGTH}</span>
                        </div>
                        <input
                          value={wing.name}
                          maxLength={FLEET_STRUCTURE_NAME_MAX_LENGTH}
                          title={`ESI wing names must be ${FLEET_STRUCTURE_NAME_MAX_LENGTH} characters or fewer.`}
                          onChange={(event) => patchDraft((next) => (next.wings[wingIndex].name = event.target.value))}
                        />
                      </div>
                      <div className="trifffleets-actions-row">
                        <button type="button" onClick={() => addSquad(wingIndex)}>
                          Add squad
                        </button>
                        <button type="button" onClick={() => removeWing(wingIndex)} disabled={draft.wings.length <= 1}>
                          Remove wing
                        </button>
                      </div>
                    </div>
                    {wing.squads.map((squad, squadIndex) => (
                      <div className="trifffleets-squad" key={`squad-${wingIndex}-${squadIndex}`}>
                        <div className="trifffleets-tree-head trifffleets-squad-head">
                          <span className="trifffleets-node-caret" aria-hidden="true" />
                          <div className="triffview-field">
                            <div className="trifffleets-field-label-row">
                              <label>Squad {squadIndex + 1}</label>
                              <span>{squad.name.trim().length}/{FLEET_STRUCTURE_NAME_MAX_LENGTH}</span>
                            </div>
                            <input
                              value={squad.name}
                              maxLength={FLEET_STRUCTURE_NAME_MAX_LENGTH}
                              title={`ESI squad names must be ${FLEET_STRUCTURE_NAME_MAX_LENGTH} characters or fewer.`}
                              onChange={(event) => patchDraft((next) => (next.wings[wingIndex].squads[squadIndex].name = event.target.value))}
                            />
                          </div>
                          <div className="trifffleets-actions-row">
                            <button type="button" onClick={() => addMember(wingIndex, squadIndex)}>
                              Add character
                            </button>
                            <button type="button" onClick={() => removeSquad(wingIndex, squadIndex)} disabled={wing.squads.length <= 1}>
                              Remove squad
                            </button>
                          </div>
                        </div>
                        <div className="trifffleets-members">
                          {squad.members.map((member, memberIndex) => (
                            <div className="trifffleets-member-row" key={`${wingIndex}-${squadIndex}-${memberIndex}`}>
                              <span className="trifffleets-member-rail" aria-hidden="true" />
                              <input
                                placeholder="Character name"
                                value={member.characterName}
                                onChange={(event) =>
                                  patchDraft((next) => {
                                    next.wings[wingIndex].squads[squadIndex].members[memberIndex].characterName = event.target.value;
                                  })
                                }
                              />
                              <select
                                value={member.role || "squad_member"}
                                onChange={(event) =>
                                  patchDraft((next) => {
                                    next.wings[wingIndex].squads[squadIndex].members[memberIndex].role = event.target.value;
                                  })
                                }
                              >
                                {ROLE_OPTIONS.map(([value, label]) => (
                                  <option value={value} key={value}>
                                    {label}
                                  </option>
                                ))}
                              </select>
                              <button type="button" onClick={() => removeMember(wingIndex, squadIndex, memberIndex)}>
                                Remove
                              </button>
                            </div>
                          ))}
                          {!squad.members.length ? <div className="eve-settings-empty-inline">No characters in this squad yet.</div> : null}
                        </div>
                      </div>
                    ))}
                  </section>
                ))}
              </div>
            </div>
          ) : null}

          {activeSection === "results" ? (
            <div className="trifffleets-stack">
              <div className="triffview-band">
                <div className="triffview-band-top">
                  <div>
                    <h2>Apply result log</h2>
                    <p>Invite acceptance is manual inside EVE. This log only reports ESI actions.</p>
                  </div>
                  <button type="button" onClick={copyLog} disabled={!state.applyResult?.results.length}>
                    Copy log
                  </button>
                </div>
                {state.applyResult?.results.length ? (
                  <div className="trifffleets-result-list">
                    {state.applyResult.results.map((item, index) => (
                      <div className="trifffleets-result-row" key={`${item.kind}-${item.id}-${index}`}>
                        <span>{item.kind}</span>
                        <strong>{item.name || item.id || "System"}</strong>
                        <em className={statusClass(item.status)}>{item.status}</em>
                        <p>{item.detail}</p>
                      </div>
                    ))}
                  </div>
                ) : (
                  <div className="eve-settings-empty">No apply results yet.</div>
                )}
              </div>
            </div>
          ) : null}
        </div>
      </section>
    </div>
  );
}
