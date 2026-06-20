import React, { useEffect, useMemo, useRef, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";

type ServerInfo = {
  path: string;
  name: string;
  key: string;
  exists: boolean;
};

type ProfileInfo = {
  path: string;
  name: string;
  fileCount: number;
  modifiedUtc: string;
  note?: string;
};

type SettingsFileInfo = {
  path: string;
  id: string;
  name: string;
  size: number;
  modifiedUtc: string;
  note?: string;
};

type BackupInfo = {
  path: string;
  kind: string;
  label: string;
  sourcePath: string;
  createdUtc: string;
  size: number;
};

type EveSettingsState = {
  defaultRoot: string;
  rootPath: string;
  selectedServerPath: string;
  selectedProfilePath: string;
  eveRunning: boolean;
  servers: ServerInfo[];
  profiles: ProfileInfo[];
  characters: SettingsFileInfo[];
  accounts: SettingsFileInfo[];
  backups: BackupInfo[];
  notes: Record<string, string>;
};

type PendingAction = {
  title: string;
  body: string;
  button: string;
  message: Record<string, unknown>;
  danger?: boolean;
  success?: string;
};

const EMPTY_STATE: EveSettingsState = {
  defaultRoot: "",
  rootPath: "",
  selectedServerPath: "",
  selectedProfilePath: "",
  eveRunning: false,
  servers: [],
  profiles: [],
  characters: [],
  accounts: [],
  backups: [],
  notes: {},
};

const SECTIONS = [
  ["overview", "Overview"],
  ["characters", "Characters"],
  ["accounts", "Accounts"],
  ["backups", "Backups"],
  ["advanced", "Advanced"],
] as const;

function send(type: string, payload: Record<string, unknown> = {}) {
  postNative({ type, ...payload });
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

function formatBytes(value?: number) {
  const bytes = Number(value) || 0;
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

function pathLeaf(path: string) {
  return String(path || "").split(/[\\/]/).filter(Boolean).pop() || path || "-";
}

function selectedOrFirst<T extends { path: string }>(items: T[], selectedPath: string) {
  return items.find((item) => item.path === selectedPath) || items[0] || null;
}

function serverLabel(server: ServerInfo) {
  return server.name || server.key || pathLeaf(server.path) || "Unknown server";
}

function profileLabel(profile: ProfileInfo) {
  return profile.name || pathLeaf(profile.path) || "Unknown settings set";
}

function fileIdLabel(file: SettingsFileInfo) {
  return file.id || pathLeaf(file.path).replace(/^core_(char|user)_/i, "").replace(/\.dat$/i, "") || "-";
}

function NoteInput({
  type,
  id,
  value,
}: {
  type: string;
  id: string;
  value?: string;
}) {
  const [draft, setDraft] = useState(value || "");

  useEffect(() => {
    setDraft(value || "");
  }, [value]);

  return (
    <input
      value={draft}
      placeholder="Note"
      onChange={(event) => setDraft(event.target.value)}
      onBlur={() => send("eve-settings:set-note", { entityType: type, entityId: id, note: draft })}
    />
  );
}

function ConfirmModal({
  action,
  onCancel,
  onConfirm,
  eveRunning,
}: {
  action: PendingAction;
  onCancel: () => void;
  onConfirm: () => void;
  eveRunning: boolean;
}) {
  return (
    <div className="triffview-modal-backdrop eve-settings-modal-backdrop">
      <section className="triffview-hotkey-modal eve-settings-modal">
        <header>
          <div>
            <h3>{action.title}</h3>
            <p>{action.body}</p>
          </div>
          <button type="button" onClick={onCancel}>
            Close
          </button>
        </header>
        <div className="eve-settings-modal-body" data-hud-scroll>
          {eveRunning ? (
            <div className="triffview-warning">
              <strong>EVE is running.</strong>
              <span>These files are safest to copy or restore while the EVE client is fully closed.</span>
            </div>
          ) : null}
          <p>
            TriffHUD will create a backup before overwriting or deleting live settings. The original `.dat` files are copied
            as-is; the HUD does not parse or modify their contents.
          </p>
        </div>
        <footer className="eve-settings-modal-actions">
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className={action.danger ? "danger-action" : "primary-action"} onClick={onConfirm}>
            {action.button}
          </button>
        </footer>
      </section>
    </div>
  );
}

function FileCopyPanel({
  label,
  files,
  source,
  target,
  onSource,
  onTarget,
  onCopy,
}: {
  label: string;
  files: SettingsFileInfo[];
  source: string;
  target: string;
  onSource: (value: string) => void;
  onTarget: (value: string) => void;
  onCopy: () => void;
}) {
  return (
    <div className="triffview-band eve-settings-copy-panel">
      <div className="triffview-band-top">
        <div>
          <h2>{label} copy</h2>
          <p>Copy one settings file over another in the selected settings set.</p>
        </div>
        <button type="button" disabled={files.length < 2 || !source || !target || source === target} onClick={onCopy}>
          Copy source to target
        </button>
      </div>
      <div className="eve-settings-copy-grid">
        <label className="triffview-field">
          <span>Source</span>
          <select value={source} onChange={(event) => onSource(event.target.value)}>
            {files.map((file) => (
              <option key={file.path} value={file.path}>
                {file.name || pathLeaf(file.path)} ({fileIdLabel(file)})
              </option>
            ))}
          </select>
        </label>
        <label className="triffview-field">
          <span>Target</span>
          <select value={target} onChange={(event) => onTarget(event.target.value)}>
            {files.map((file) => (
              <option key={file.path} value={file.path}>
                {file.name || pathLeaf(file.path)} ({fileIdLabel(file)})
              </option>
            ))}
          </select>
        </label>
      </div>
    </div>
  );
}

function CharacterCopyPanel({
  files,
  source,
  selectedTargets,
  onSource,
  onTargets,
  onCopySelected,
  onCopyAll,
}: {
  files: SettingsFileInfo[];
  source: string;
  selectedTargets: string[];
  onSource: (value: string) => void;
  onTargets: (value: string[]) => void;
  onCopySelected: () => void;
  onCopyAll: () => void;
}) {
  const [targetQuery, setTargetQuery] = useState("");
  const lastRangeTarget = useRef<string | null>(null);
  const targetFiles = files.filter((file) => file.path !== source);
  const targetPathSet = new Set(targetFiles.map((file) => file.path));
  const selectedSet = new Set(selectedTargets.filter((path) => targetPathSet.has(path)));
  const normalizedQuery = targetQuery.trim().toLowerCase();
  const visibleTargetFiles = normalizedQuery
    ? targetFiles.filter((file) => {
        const name = (file.name || pathLeaf(file.path)).toLowerCase();
        const id = fileIdLabel(file).toLowerCase();
        return name.includes(normalizedQuery) || id.includes(normalizedQuery);
      })
    : targetFiles;
  const visibleTargetPaths = visibleTargetFiles.map((file) => file.path);
  const selectedCount = selectedSet.size;
  const visibleSelectedCount = visibleTargetPaths.filter((path) => selectedSet.has(path)).length;

  function applyTargets(paths: Iterable<string>) {
    const next = new Set(paths);
    onTargets(targetFiles.filter((file) => next.has(file.path)).map((file) => file.path));
  }

  function toggleTarget(path: string, event: React.MouseEvent<HTMLElement>) {
    if (event.shiftKey && lastRangeTarget.current) {
      const clickedIndex = visibleTargetPaths.indexOf(path);
      const anchorIndex = visibleTargetPaths.indexOf(lastRangeTarget.current);
      if (clickedIndex >= 0 && anchorIndex >= 0) {
        const [start, end] = clickedIndex < anchorIndex ? [clickedIndex, anchorIndex] : [anchorIndex, clickedIndex];
        const shouldSelect = !selectedSet.has(path);
        const next = new Set(selectedSet);
        visibleTargetPaths.slice(start, end + 1).forEach((targetPath) => {
          if (shouldSelect) {
            next.add(targetPath);
          } else {
            next.delete(targetPath);
          }
        });
        lastRangeTarget.current = path;
        applyTargets(next);
        return;
      }
    }

    const next = new Set(selectedSet);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }
    lastRangeTarget.current = path;
    applyTargets(next);
  }

  function selectAllTargets() {
    lastRangeTarget.current = null;
    onTargets(targetFiles.map((file) => file.path));
  }

  function selectVisibleTargets() {
    if (!visibleTargetPaths.length) {
      return;
    }
    lastRangeTarget.current = null;
    applyTargets([...Array.from(selectedSet), ...visibleTargetPaths]);
  }

  function clearTargets() {
    lastRangeTarget.current = null;
    onTargets([]);
  }

  return (
    <div className="triffview-band eve-settings-copy-panel eve-settings-character-copy">
      <div className="triffview-band-top">
        <div>
          <h2>Character copy</h2>
          <p>Copy one character's settings to selected characters, or to every other character in this settings set.</p>
        </div>
      </div>
      <div className="eve-settings-bulk-copy-grid">
        <label className="triffview-field">
          <span>Source character</span>
          <select value={source} onChange={(event) => onSource(event.target.value)}>
            {files.map((file) => (
              <option key={file.path} value={file.path}>
                {file.name || pathLeaf(file.path)} ({fileIdLabel(file)})
              </option>
            ))}
          </select>
        </label>

        <div className="triffview-field eve-settings-target-picker">
          <div className="eve-settings-target-picker-head">
            <div>
              <span>Target characters</span>
              <small>
                {selectedCount} selected
                {normalizedQuery ? ` / ${visibleTargetFiles.length} visible` : ` / ${targetFiles.length} available`}
              </small>
            </div>
            <div className="eve-settings-inline-actions">
              <button type="button" disabled={!targetFiles.length || selectedCount === targetFiles.length} onClick={selectAllTargets}>
                Select all
              </button>
              {normalizedQuery ? (
                <button
                  type="button"
                  disabled={!visibleTargetPaths.length || visibleSelectedCount === visibleTargetPaths.length}
                  onClick={selectVisibleTargets}
                >
                  Select visible
                </button>
              ) : null}
              <button type="button" disabled={!selectedCount} onClick={clearTargets}>
                Clear
              </button>
            </div>
          </div>
          <input
            className="eve-settings-target-search"
            value={targetQuery}
            placeholder="Search characters..."
            onChange={(event) => setTargetQuery(event.target.value)}
          />
          <div className="eve-settings-target-list" data-hud-scroll>
            {visibleTargetFiles.map((file) => (
              <button
                key={file.path}
                type="button"
                className={selectedSet.has(file.path) ? "eve-settings-target-row is-selected" : "eve-settings-target-row"}
                role="checkbox"
                aria-checked={selectedSet.has(file.path)}
                onClick={(event) => toggleTarget(file.path, event)}
              >
                <span className="eve-settings-target-check" aria-hidden="true" />
                <span className="eve-settings-target-name">{file.name || pathLeaf(file.path)}</span>
                <small>{fileIdLabel(file)}</small>
              </button>
            ))}
            {!targetFiles.length ? <div className="eve-settings-empty-inline">No target characters available.</div> : null}
            {targetFiles.length && !visibleTargetFiles.length ? <div className="eve-settings-empty-inline">No characters match that search.</div> : null}
          </div>
          <div className="eve-settings-target-footer">
            <span>
              {selectedCount} of {targetFiles.length} targets selected
            </span>
            <div className="eve-settings-copy-actions">
              <button type="button" disabled={selectedCount === 0 || !source} onClick={onCopySelected}>
                Copy to selected
              </button>
              <button type="button" disabled={targetFiles.length === 0 || !source} onClick={onCopyAll}>
                Copy to all others
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function SettingsFileTable({
  files,
  kind,
  onBackup,
  onReveal,
}: {
  files: SettingsFileInfo[];
  kind: "character" | "account";
  onBackup: (file: SettingsFileInfo) => void;
  onReveal: (path: string) => void;
}) {
  if (!files.length) {
    return <div className="eve-settings-empty">No {kind} settings files found in this settings set.</div>;
  }

  return (
    <div className="eve-settings-table-wrap" data-hud-scroll>
      <table className="eve-settings-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>ID</th>
            <th>Modified</th>
            <th>Size</th>
            <th>Note</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.path}>
              <td>{file.name}</td>
              <td>{fileIdLabel(file)}</td>
              <td>{formatDate(file.modifiedUtc)}</td>
              <td>{formatBytes(file.size)}</td>
              <td>
                <NoteInput type={kind} id={fileIdLabel(file)} value={file.note} />
              </td>
              <td>
                <div className="eve-settings-inline-actions">
                  <button type="button" onClick={() => onBackup(file)}>
                    Backup
                  </button>
                  <button type="button" onClick={() => onReveal(file.path)}>
                    Reveal
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export default function EveSettings() {
  const [state, setState] = useState<EveSettingsState>(EMPTY_STATE);
  const [section, setSection] = useState<(typeof SECTIONS)[number][0]>("overview");
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null);
  const [characterSource, setCharacterSource] = useState("");
  const [selectedCharacterTargets, setSelectedCharacterTargets] = useState<string[]>([]);
  const [accountSource, setAccountSource] = useState("");
  const [accountTarget, setAccountTarget] = useState("");
  const [profileName, setProfileName] = useState("TriffHUD");
  const [renameName, setRenameName] = useState("");
  const pendingSuccessRef = useRef("");
  const successTimerRef = useRef<number | null>(null);

  const selectedServer = useMemo(
    () => selectedOrFirst(state.servers, state.selectedServerPath),
    [state.servers, state.selectedServerPath]
  );
  const selectedProfile = useMemo(
    () => selectedOrFirst(state.profiles, state.selectedProfilePath),
    [state.profiles, state.selectedProfilePath]
  );

  function showSuccess(message: string) {
    if (successTimerRef.current != null) {
      window.clearTimeout(successTimerRef.current);
    }
    setSuccess(message);
    successTimerRef.current = window.setTimeout(() => {
      setSuccess("");
      successTimerRef.current = null;
    }, 5200);
  }

  useEffect(() => {
    send("eve-settings:get-state");
    const unsubscribe = onNativeMessage((message: any) => {
      if (message?.type === "eve-settings:state") {
        setState({
          ...EMPTY_STATE,
          ...message,
          servers: Array.isArray(message.servers) ? message.servers : [],
          profiles: Array.isArray(message.profiles) ? message.profiles : [],
          characters: Array.isArray(message.characters) ? message.characters : [],
          accounts: Array.isArray(message.accounts) ? message.accounts : [],
          backups: Array.isArray(message.backups) ? message.backups : [],
          notes: message.notes || {},
        });
        setError("");
        if (pendingSuccessRef.current) {
          showSuccess(pendingSuccessRef.current);
          pendingSuccessRef.current = "";
        }
      }

      if (message?.type === "eve-settings:error") {
        pendingSuccessRef.current = "";
        setSuccess("");
        setError(message.message || "EVE Settings action failed.");
      }
    });
    return () => {
      unsubscribe();
      if (successTimerRef.current != null) {
        window.clearTimeout(successTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    if (!state.characters.length) {
      setCharacterSource("");
      setSelectedCharacterTargets([]);
      return;
    }
    setCharacterSource((current) => (state.characters.some((file) => file.path === current) ? current : state.characters[0].path));
  }, [state.characters]);

  useEffect(() => {
    setSelectedCharacterTargets((current) =>
      current.filter((path) => path !== characterSource && state.characters.some((file) => file.path === path))
    );
  }, [state.characters, characterSource]);

  useEffect(() => {
    if (!state.accounts.length) {
      setAccountSource("");
      setAccountTarget("");
      return;
    }
    setAccountSource((current) => (state.accounts.some((file) => file.path === current) ? current : state.accounts[0].path));
    setAccountTarget((current) => {
      if (state.accounts.some((file) => file.path === current)) return current;
      return state.accounts[1]?.path || state.accounts[0].path;
    });
  }, [state.accounts]);

  useEffect(() => {
    setRenameName(selectedProfile ? profileLabel(selectedProfile) : "");
  }, [selectedProfile?.path]);

  function ask(action: PendingAction) {
    setSuccess("");
    setError("");
    setPendingAction(action);
  }

  function confirmAndSend(action: PendingAction) {
    const message = action.message;
    pendingSuccessRef.current = action.success || "";
    setSuccess("");
    send(String(message.type), Object.fromEntries(Object.entries(message).filter(([key]) => key !== "type")));
    setPendingAction(null);
  }

  function copyFile(sourcePath: string, targetPath: string, label: string) {
    const files = label === "account" ? state.accounts : state.characters;
    const source = files.find((file) => file.path === sourcePath);
    const target = files.find((file) => file.path === targetPath);
    const labelText = label.charAt(0).toUpperCase() + label.slice(1);
    ask({
      title: `Copy ${label} settings`,
      body: "The target settings file will be backed up, then overwritten with the source file.",
      button: "Copy settings",
      danger: true,
      success: `${labelText} settings copied from ${source?.name || "source"} to ${target?.name || "target"}.`,
      message: { type: "eve-settings:copy-file", sourcePath, targetPath },
    });
  }

  function copyCharacterTargets(targetPaths: string[]) {
    const targets = targetPaths.filter((path) => path && path !== characterSource);
    const source = state.characters.find((file) => file.path === characterSource);
    if (!source || !targets.length) return;

    ask({
      title: "Copy character settings",
      body: `${source.name || "The source character"} will overwrite ${targets.length} target character${targets.length === 1 ? "" : "s"}.`,
      button: targets.length === 1 ? "Copy settings" : `Copy to ${targets.length}`,
      danger: true,
      success: `${source.name || "Character settings"} copied to ${targets.length} target character${targets.length === 1 ? "" : "s"}.`,
      message: { type: "eve-settings:copy-file-to-targets", sourcePath: characterSource, targetPaths: targets },
    });
  }

  function renderOverview() {
    const hasMultipleSettingsSets = state.profiles.length > 1;

    return (
      <>
        <div className="triffview-band">
          <div className="triffview-band-top">
            <div>
              <h2>Folder</h2>
              <p>{state.rootPath || "No EVE settings folder selected."}</p>
            </div>
            <div className="triffview-actions">
              <button type="button" onClick={() => send("eve-settings:select-folder")}>
                Select folder
              </button>
              <button type="button" onClick={() => send("eve-settings:refresh")}>
                Refresh
              </button>
              <button type="button" disabled={!state.rootPath} onClick={() => send("eve-settings:show-in-folder", { path: state.rootPath })}>
                Reveal
              </button>
            </div>
          </div>
        </div>

        {state.eveRunning ? (
          <div className="triffview-warning">
            <strong>EVE client detected.</strong>
            <span>Backups and browsing are fine. Copy, restore, rename, and delete actions are safest after closing EVE.</span>
          </div>
        ) : null}

        <div className="triffview-grid">
          <label className="triffview-field">
            <span>Server</span>
            <select
              value={selectedServer?.path || ""}
              disabled={!state.servers.length}
              onChange={(event) => send("eve-settings:set-server", { serverPath: event.target.value })}
            >
              {!state.servers.length ? <option value="">No servers found</option> : null}
              {state.servers.map((server) => (
                <option key={server.path} value={server.path}>
                  {serverLabel(server)}
                </option>
              ))}
            </select>
          </label>
          {hasMultipleSettingsSets ? (
            <label className="triffview-field">
              <span>Settings set</span>
              <select
                value={selectedProfile?.path || ""}
                disabled={!state.profiles.length}
                onChange={(event) => send("eve-settings:set-profile", { profilePath: event.target.value })}
              >
                {state.profiles.map((profile) => (
                  <option key={profile.path} value={profile.path}>
                    {profileLabel(profile)} ({profile.fileCount})
                  </option>
                ))}
              </select>
            </label>
          ) : (
            <div className="triffview-field eve-settings-readonly-field">
              <span>Settings set</span>
              <strong>{selectedProfile ? profileLabel(selectedProfile) : "No settings sets found"}</strong>
              <small>{selectedProfile ? `${selectedProfile.fileCount} settings files` : "Use Advanced if you need to create one."}</small>
            </div>
          )}
        </div>

        <div className="eve-settings-stat-grid">
          <div>
            <span>Characters</span>
            <strong>{state.characters.length}</strong>
          </div>
          <div>
            <span>Accounts</span>
            <strong>{state.accounts.length}</strong>
          </div>
          <div>
            <span>Settings sets</span>
            <strong>{state.profiles.length}</strong>
          </div>
          <div>
            <span>Backups</span>
            <strong>{state.backups.length}</strong>
          </div>
        </div>
      </>
    );
  }

  function renderCharacters() {
    const allTargets = state.characters
      .filter((file) => file.path !== characterSource)
      .map((file) => file.path);

    return (
      <>
        <CharacterCopyPanel
          files={state.characters}
          source={characterSource}
          selectedTargets={selectedCharacterTargets}
          onSource={setCharacterSource}
          onTargets={setSelectedCharacterTargets}
          onCopySelected={() => copyCharacterTargets(selectedCharacterTargets)}
          onCopyAll={() => copyCharacterTargets(allTargets)}
        />
        <SettingsFileTable
          kind="character"
          files={state.characters}
          onBackup={(file) =>
            ask({
              title: "Backup character settings",
              body: `${file.name} will be copied into a TriffHUD backup ZIP.`,
              button: "Back up",
              message: { type: "eve-settings:backup-file", filePath: file.path },
            })
          }
          onReveal={(path) => send("eve-settings:show-in-folder", { path })}
        />
      </>
    );
  }

  function renderAccounts() {
    return (
      <>
        <FileCopyPanel
          label="Account"
          files={state.accounts}
          source={accountSource}
          target={accountTarget}
          onSource={setAccountSource}
          onTarget={setAccountTarget}
          onCopy={() => copyFile(accountSource, accountTarget, "account")}
        />
        <SettingsFileTable
          kind="account"
          files={state.accounts}
          onBackup={(file) =>
            ask({
              title: "Backup account settings",
              body: `${file.name} will be copied into a TriffHUD backup ZIP.`,
              button: "Back up",
              message: { type: "eve-settings:backup-file", filePath: file.path },
            })
          }
          onReveal={(path) => send("eve-settings:show-in-folder", { path })}
        />
      </>
    );
  }

  function renderSettingsSets() {
    return (
      <>
        <div className="triffview-grid">
          <label className="triffview-field">
            <span>Create settings set</span>
            <div className="eve-settings-input-action">
              <input value={profileName} onChange={(event) => setProfileName(event.target.value)} />
              <button type="button" onClick={() => send("eve-settings:create-profile", { name: profileName })}>
                Create
              </button>
            </div>
          </label>
          <label className="triffview-field">
            <span>Rename selected settings set</span>
            <div className="eve-settings-input-action">
              <input value={renameName} disabled={!selectedProfile} onChange={(event) => setRenameName(event.target.value)} />
              <button
                type="button"
                disabled={!selectedProfile || !renameName.trim()}
                onClick={() =>
                  ask({
                    title: "Rename settings set",
                    body: `${selectedProfile ? profileLabel(selectedProfile) : "This settings set"} will be renamed.`,
                    button: "Rename",
                    message: { type: "eve-settings:rename-profile", profilePath: selectedProfile?.path, name: renameName },
                  })
                }
              >
                Rename
              </button>
            </div>
          </label>
        </div>

        <div className="eve-settings-table-wrap" data-hud-scroll>
          <table className="eve-settings-table">
            <thead>
              <tr>
                <th>Settings set</th>
                <th>Files</th>
                <th>Modified</th>
                <th>Note</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {state.profiles.map((profile) => (
                <tr key={profile.path} className={profile.path === state.selectedProfilePath ? "is-selected" : ""}>
                  <td>{profileLabel(profile)}</td>
                  <td>{profile.fileCount}</td>
                  <td>{formatDate(profile.modifiedUtc)}</td>
                  <td>
                    <NoteInput type="profile" id={profile.path} value={profile.note} />
                  </td>
                  <td>
                    <div className="eve-settings-inline-actions">
                      <button type="button" onClick={() => send("eve-settings:set-profile", { profilePath: profile.path })}>
                        Select
                      </button>
                      <button
                        type="button"
                        onClick={() =>
                          ask({
                            title: "Duplicate settings set",
                            body: `${profileLabel(profile)} will be copied into a new settings set named ${profileName || "TriffHUD"}.`,
                            button: "Duplicate",
                            message: { type: "eve-settings:duplicate-profile", profilePath: profile.path, name: profileName },
                          })
                        }
                      >
                        Duplicate
                      </button>
                      <button
                        type="button"
                        onClick={() =>
                          ask({
                            title: "Backup settings set",
                            body: `${profileLabel(profile)} will be copied into a TriffHUD backup ZIP.`,
                            button: "Back up",
                            message: { type: "eve-settings:backup-profile", profilePath: profile.path },
                          })
                        }
                      >
                        Backup
                      </button>
                      <button type="button" onClick={() => send("eve-settings:show-in-folder", { path: profile.path })}>
                        Reveal
                      </button>
                      <button
                        type="button"
                        onClick={() =>
                          ask({
                            title: "Delete settings set",
                            body: `${profileLabel(profile)} will be backed up and then deleted from the EVE settings folder.`,
                            button: "Delete",
                            danger: true,
                            message: { type: "eve-settings:delete-profile", profilePath: profile.path },
                          })
                        }
                      >
                        Delete
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </>
    );
  }

  function renderBackups() {
    if (!state.backups.length) return <div className="eve-settings-empty">No EVE settings backups yet.</div>;

    return (
      <div className="eve-settings-table-wrap" data-hud-scroll>
        <table className="eve-settings-table">
          <thead>
            <tr>
              <th>Backup</th>
              <th>Kind</th>
              <th>Created</th>
              <th>Size</th>
              <th>Source</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            {state.backups.map((backup) => (
              <tr key={backup.path}>
                <td>{backup.label || pathLeaf(backup.path)}</td>
                <td>{backup.kind}</td>
                <td>{formatDate(backup.createdUtc)}</td>
                <td>{formatBytes(backup.size)}</td>
                <td title={backup.sourcePath}>{pathLeaf(backup.sourcePath)}</td>
                <td>
                  <div className="eve-settings-inline-actions">
                    <button
                      type="button"
                      onClick={() =>
                        ask({
                          title: "Restore backup",
                          body: `${backup.label || "This backup"} will be restored to its original EVE settings path.`,
                          button: "Restore",
                          danger: true,
                          message: { type: "eve-settings:restore-backup", backupPath: backup.path },
                        })
                      }
                    >
                      Restore
                    </button>
                    <button type="button" onClick={() => send("eve-settings:show-in-folder", { path: backup.path })}>
                      Reveal
                    </button>
                    <button
                      type="button"
                      onClick={() =>
                        ask({
                          title: "Delete backup",
                          body: `${backup.label || "This backup"} will be permanently removed from TriffHUD backups.`,
                          button: "Delete",
                          danger: true,
                          message: { type: "eve-settings:delete-backup", backupPath: backup.path },
                        })
                      }
                    >
                      Delete
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }

  function renderNotes() {
    const notes = Object.entries(state.notes || {});
    if (!notes.length) return <div className="eve-settings-empty">No saved notes yet. Add notes from Characters, Accounts, or Advanced.</div>;
    return (
      <div className="eve-settings-table-wrap" data-hud-scroll>
        <table className="eve-settings-table">
          <thead>
            <tr>
              <th>Key</th>
              <th>Note</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            {notes.map(([key, note]) => (
              <tr key={key}>
                <td>{key}</td>
                <td>{note}</td>
                <td>
                  <button
                    type="button"
                    onClick={() => {
                      const [entityType, ...rest] = key.split(":");
                      send("eve-settings:set-note", { entityType, entityId: rest.join(":"), note: "" });
                    }}
                  >
                    Clear
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }

  function renderAdvanced() {
    return (
      <>
        <div className="triffview-band eve-settings-advanced-intro">
          <div className="triffview-band-top">
            <div>
              <h2>Advanced EVE settings folders</h2>
              <p>
                Most pilots should leave this alone and use the Default settings set. These controls are for people who
                intentionally keep multiple EVE UI/settings folders.
              </p>
            </div>
          </div>
        </div>

        <div className="triffview-band eve-settings-advanced-section">
          <div className="triffview-band-top">
            <div>
              <h2>Settings sets</h2>
              <p>Create, duplicate, back up, reveal, or delete EVE settings folders.</p>
            </div>
          </div>
          <div className="eve-settings-advanced-body">{renderSettingsSets()}</div>
        </div>

        <div className="triffview-band eve-settings-advanced-section">
          <div className="triffview-band-top">
            <div>
              <h2>Saved notes</h2>
              <p>Local labels attached to characters, accounts, backups, and settings sets.</p>
            </div>
          </div>
          <div className="eve-settings-advanced-body">{renderNotes()}</div>
        </div>
      </>
    );
  }

  function renderSection() {
    if (section === "overview") return renderOverview();
    if (section === "characters") return renderCharacters();
    if (section === "accounts") return renderAccounts();
    if (section === "backups") return renderBackups();
    return renderAdvanced();
  }

  return (
    <div className="triffview-settings eve-settings" data-hud-scroll data-hud-select-text-controls="true">
      <section className="triffview-settings-shell">
        <aside className="triffview-side-nav">
          <div className="triffview-nav-brand">
            <h2>EVE Settings</h2>
            <p>Local EVE character, account, and settings file management.</p>
          </div>
          <nav aria-label="EVE settings sections">
            {SECTIONS.map(([id, label]) => (
              <button key={id} type="button" className={section === id ? "is-active" : ""} onClick={() => setSection(id)}>
                {label}
              </button>
            ))}
          </nav>
          <div className="triffview-nav-actions">
            <button type="button" onClick={() => send("eve-settings:refresh")}>
              Refresh
            </button>
            <button type="button" onClick={() => send("eve-settings:select-folder")}>
              Select folder
            </button>
          </div>
        </aside>

        <section className="triffview-section-content" data-hud-scroll>
          <header className="triffview-section-header eve-settings-header">
            <div>
              <h2>{SECTIONS.find(([id]) => id === section)?.[1] || "Overview"}</h2>
              <p>
                {selectedServer ? serverLabel(selectedServer) : "No server"} /{" "}
                {selectedProfile ? `Settings set: ${profileLabel(selectedProfile)}` : "No settings set"}
              </p>
            </div>
            <span className={state.eveRunning ? "eve-settings-running is-running" : "eve-settings-running"}>
              {state.eveRunning ? "EVE running" : "EVE closed"}
            </span>
          </header>

          {error ? (
            <div className="triffview-warning">
              <strong>Action failed.</strong>
              <span>{error}</span>
            </div>
          ) : null}

          {success ? (
            <div className="eve-settings-success">
              <strong>Copy complete.</strong>
              <span>{success}</span>
            </div>
          ) : null}

          {!state.rootPath ? (
            <div className="triffview-band">
              <div className="triffview-band-top">
                <div>
                  <h2>Select your EVE settings folder</h2>
                  <p>Default path: {state.defaultRoot || "%LOCALAPPDATA%\\CCP\\EVE"}</p>
                </div>
                <button type="button" onClick={() => send("eve-settings:select-folder")}>
                  Select folder
                </button>
              </div>
            </div>
          ) : (
            renderSection()
          )}
        </section>
      </section>

      {pendingAction ? (
        <ConfirmModal
          action={pendingAction}
          eveRunning={state.eveRunning}
          onCancel={() => setPendingAction(null)}
          onConfirm={() => confirmAndSend(pendingAction)}
        />
      ) : null}
    </div>
  );
}
