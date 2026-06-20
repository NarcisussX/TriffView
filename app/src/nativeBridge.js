const listeners = new Set();
export const HUD_OPEN_LINK_EVENT = "triff:hud-open-link";

function hasWebViewBridge() {
  return Boolean(window.chrome?.webview?.postMessage);
}

export function postNative(message) {
  if (hasWebViewBridge()) {
    window.chrome.webview.postMessage(message);
    return true;
  }
  return false;
}

export function onNativeMessage(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function requestHudDeepLink(url) {
  try {
    const event = new CustomEvent(HUD_OPEN_LINK_EVENT, {
      cancelable: true,
      detail: { url },
    });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  } catch {
    return false;
  }
}

export function openExternalUrl(url) {
  if (requestHudDeepLink(url)) return;
  if (!postNative({ type: "open-external", url })) {
    window.open(url, "_blank", "noopener,noreferrer");
  }
}

export function setNativeClickThrough(enabled) {
  postNative({ type: "set-click-through", enabled });
}

export function toggleNativeClickThrough() {
  postNative({ type: "toggle-click-through" });
}

export function hideHud() {
  postNative({ type: "hide" });
}

export async function copyText(text) {
  if (postNative({ type: "copy-text", text })) return true;
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

export async function readClipboard() {
  try {
    return await navigator.clipboard.readText();
  } catch {
    postNative({ type: "read-clipboard" });
    return "";
  }
}

let activePointerTarget = null;
let activePointerButtons = 0;
let activeScrollDrag = null;
let pendingClipboardPasteTarget = null;
let activeSelectPopup = null;
let suppressNextPointerUp = false;
let activeTextSelectionDrag = null;
let activeTextareaResize = null;
let textCaretElement = null;
let textCaretTarget = null;
let textMeasureCanvas = null;
let hoveredInteractiveElement = null;
let pressedInteractiveElement = null;
let syntheticHoverTarget = null;
const textSelectionState = new WeakMap();

const HUD_SCROLL_SELECTOR = "[data-hud-scroll]";
const HUD_COARSE_POINTER_SELECTOR = "[data-hud-coarse-pointer]";
const HUD_RESIZE_HANDLE_SELECTOR = "[data-hud-resize-handle], .resize-handle";
const HUD_INTERACTIVE_SELECTOR = [
  "button",
  "a[href]",
  "[role='button']",
  "summary",
  "select",
  "input[type='button']",
  "input[type='submit']",
  "input[type='reset']",
  "input[type='checkbox']",
  "input[type='radio']",
].join(",");
const COARSE_POINTER_MOVE_INTERVAL_MS = 50;
const TEXTAREA_RESIZE_HIT_SIZE = 18;
let lastCoarsePointerMoveAt = 0;

function dispatchMouseLikeEvent(target, eventName, input) {
  if (!target) return false;
  const event = new MouseEvent(eventName, {
    bubbles: true,
    cancelable: true,
    clientX: input.x,
    clientY: input.y,
    button: input.button || 0,
    buttons: input.buttons || 0,
    detail: input.clickCount || 1,
    view: window,
  });
  return target.dispatchEvent(event);
}

function dispatchMouseTransitionEvent(target, eventName, input, relatedTarget = null) {
  if (!target) return false;
  const event = new MouseEvent(eventName, {
    bubbles: true,
    cancelable: true,
    clientX: input?.x || 0,
    clientY: input?.y || 0,
    button: input?.button || 0,
    buttons: input?.buttons || 0,
    detail: input?.clickCount || 0,
    relatedTarget,
    view: window,
  });
  return target.dispatchEvent(event);
}

function buttonsForDomButton(button) {
  if (button === 1) return 4;
  if (button === 2) return 2;
  return 1;
}

function dispatchPointerEvent(target, eventName, input) {
  if (!target) return false;
  const PointerCtor = window.PointerEvent || MouseEvent;
  const event = new PointerCtor(eventName, {
    bubbles: true,
    cancelable: true,
    clientX: input.x,
    clientY: input.y,
    button: input.button || 0,
    buttons: input.buttons || 0,
    detail: input.clickCount || 1,
    pointerId: 1,
    pointerType: "mouse",
    isPrimary: true,
    view: window,
  });
  return target.dispatchEvent(event);
}

function dispatchPointerTransitionEvent(target, eventName, input, relatedTarget = null) {
  if (!target) return false;
  const PointerCtor = window.PointerEvent || MouseEvent;
  const event = new PointerCtor(eventName, {
    bubbles: true,
    cancelable: true,
    clientX: input?.x || 0,
    clientY: input?.y || 0,
    button: input?.button || 0,
    buttons: input?.buttons || 0,
    detail: input?.clickCount || 0,
    pointerId: 1,
    pointerType: "mouse",
    isPrimary: true,
    relatedTarget,
    view: window,
  });
  return target.dispatchEvent(event);
}

function updateSyntheticHover(nextTarget, input) {
  if (activePointerTarget) return;
  if (nextTarget === syntheticHoverTarget) return;

  const previousTarget = syntheticHoverTarget;
  syntheticHoverTarget = nextTarget || null;

  if (previousTarget?.isConnected) {
    dispatchPointerTransitionEvent(previousTarget, "pointerout", input, nextTarget || null);
    dispatchMouseTransitionEvent(previousTarget, "mouseout", input, nextTarget || null);
  }

  if (nextTarget?.isConnected) {
    dispatchPointerTransitionEvent(nextTarget, "pointerover", input, previousTarget || null);
    dispatchMouseTransitionEvent(nextTarget, "mouseover", input, previousTarget || null);
  }
}

function focusTarget(target) {
  if (!target || typeof target.focus !== "function") return;
  try {
    target.focus({ preventScroll: true });
  } catch {
    target.focus();
  }
}

function interactiveElementFromTarget(target) {
  const element = target?.closest?.(HUD_INTERACTIVE_SELECTOR);
  if (!element) return null;
  if (element.disabled || element.getAttribute?.("aria-disabled") === "true") return null;
  return element;
}

function setHudHover(element) {
  if (hoveredInteractiveElement === element) return;
  hoveredInteractiveElement?.classList?.remove("triff-hud-hover");
  hoveredInteractiveElement = element;
  hoveredInteractiveElement?.classList?.add("triff-hud-hover");
}

function setHudPressed(element) {
  if (pressedInteractiveElement === element) return;
  pressedInteractiveElement?.classList?.remove("triff-hud-active");
  pressedInteractiveElement = element;
  pressedInteractiveElement?.classList?.add("triff-hud-active");
}

function clearHudPointerFeedback() {
  setHudHover(null);
  setHudPressed(null);
}

function pulseHudClick(element) {
  if (!element || element.disabled || element.getAttribute?.("aria-disabled") === "true") return;
  element.classList.add("triff-hud-clicked");
  window.setTimeout(() => element.classList.remove("triff-hud-clicked"), 180);
}

function currentTextControl() {
  const element = document.activeElement;
  return isTextControl(element) ? element : null;
}

function isTextControl(element) {
  if (!element) return false;
  const tag = element.tagName?.toLowerCase();
  if (tag === "textarea") return true;
  if (tag !== "input") return false;
  const type = (element.getAttribute("type") || "text").toLowerCase();
  return ["text", "search", "url", "tel", "password", "email", "number"].includes(type);
}

function selectControlFromTarget(element) {
  const select = element?.closest?.("select");
  if (!select || select.disabled) return null;
  return select;
}

function textareaFromTarget(element) {
  const textarea = element?.closest?.("textarea");
  if (!textarea || textarea.disabled || textarea.readOnly) return null;
  return textarea;
}

function numericStyleValue(value) {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function textAreaResizeHitForPoint(target, input) {
  const textarea = textareaFromTarget(target);
  if (!textarea) return null;

  const style = window.getComputedStyle(textarea);
  if (style.resize === "none") return null;

  const rect = textarea.getBoundingClientRect();
  const x = Number(input.x);
  const y = Number(input.y);
  const hitSize = Math.max(TEXTAREA_RESIZE_HIT_SIZE, Math.min(28, rect.height / 3));

  const hitsCorner =
    x >= rect.right - hitSize &&
    x <= rect.right + 2 &&
    y >= rect.bottom - hitSize &&
    y <= rect.bottom + 2;

  if (!hitsCorner) return null;

  const minHeight = numericStyleValue(style.minHeight) || 38;
  const maxHeight = numericStyleValue(style.maxHeight) || Infinity;

  return {
    element: textarea,
    startY: y,
    startHeight: rect.height,
    minHeight,
    maxHeight,
  };
}

function closeSelectPopup() {
  if (!activeSelectPopup) return;
  activeSelectPopup.popup.remove();
  activeSelectPopup = null;
}

function dispatchSelectChange(select) {
  select.dispatchEvent(new Event("input", { bubbles: true }));
  select.dispatchEvent(new Event("change", { bubbles: true }));
}

function setSelectValue(select, value) {
  const descriptor = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value");
  if (descriptor?.set) descriptor.set.call(select, value);
  else select.value = value;
  dispatchSelectChange(select);
}

function optionLabel(option) {
  return option.label || option.textContent || option.value;
}

function openSelectPopup(select) {
  closeSelectPopup();
  commitActiveTextControl();
  focusTarget(select);

  const panel = select.closest(".hud-panel");
  if (!panel) return false;

  const selectRect = select.getBoundingClientRect();
  const panelRect = panel.getBoundingClientRect();
  const popup = document.createElement("div");
  popup.className = "hud-select-popup";
  popup.dataset.hudSelectPopup = "true";

  const left = Math.max(4, Math.min(panelRect.width - selectRect.width - 4, selectRect.left - panelRect.left));
  const width = Math.max(120, selectRect.width);
  const below = panelRect.bottom - selectRect.bottom - 8;
  const above = selectRect.top - panelRect.top - 8;
  const openAbove = below < 140 && above > below;
  const maxHeight = Math.max(96, Math.min(280, openAbove ? above : below));

  popup.style.left = `${left}px`;
  popup.style.width = `${Math.min(width, panelRect.width - left - 4)}px`;
  popup.style.maxHeight = `${maxHeight}px`;
  if (openAbove) {
    popup.style.bottom = `${Math.max(4, panelRect.bottom - selectRect.top + 4)}px`;
  } else {
    popup.style.top = `${Math.max(4, selectRect.bottom - panelRect.top + 4)}px`;
  }

  Array.from(select.options).forEach((option, index) => {
    const row = document.createElement("button");
    row.type = "button";
    row.className = [
      "hud-select-option",
      option.selected ? "is-selected" : "",
      option.disabled ? "is-disabled" : "",
    ]
      .filter(Boolean)
      .join(" ");
    row.textContent = optionLabel(option);
    row.title = optionLabel(option);
    row.disabled = option.disabled;
    row.dataset.optionIndex = String(index);
    row.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (option.disabled) return;
      setSelectValue(select, option.value);
      closeSelectPopup();
    });
    popup.appendChild(row);
  });

  panel.appendChild(popup);
  activeSelectPopup = { select, popup };
  window.requestAnimationFrame(() => {
    popup.querySelector(".hud-select-option.is-selected")?.scrollIntoView?.({ block: "nearest" });
  });
  return true;
}

function maybeSelectOnFocus(target) {
  if (!isTextControl(target) || target.dataset?.hudSelectOnFocus !== "true") return;

  selectTextControl(target);
}

function maybeSelectOnDoubleClick(target, input) {
  if (!isTextControl(target) || Number(input?.clickCount || 0) < 2) return;
  const selectAllEnabled =
    target.dataset?.hudSelectOnDoubleclick === "true" ||
    Boolean(target.closest?.("[data-hud-select-text-controls='true']"));
  if (!selectAllEnabled) return;

  window.requestAnimationFrame(() => selectTextControl(target));
}

function selectTextControl(target) {
  try {
    target.select();
    textSelectionState.set(target, { start: 0, end: String(target.value || "").length });
  } catch {
    setTextSelection(target, 0, target.value?.length || 0);
  }
  updateTextCaret(target);
}

function textSelection(element) {
  const fallback = textSelectionState.get(element);
  try {
    const start = element.selectionStart;
    const end = element.selectionEnd;
    if (typeof start === "number" && typeof end === "number") {
      return { start, end };
    }
  } catch {
    // Some input types, notably number, do not expose DOM selection APIs.
  }

  const length = String(element.value || "").length;
  if (fallback) {
    return {
      start: Math.max(0, Math.min(length, fallback.start)),
      end: Math.max(0, Math.min(length, fallback.end)),
    };
  }
  return { start: length, end: length };
}

function setTextSelection(element, start, end = start) {
  const length = String(element.value || "").length;
  const next = {
    start: Math.max(0, Math.min(length, start)),
    end: Math.max(0, Math.min(length, end)),
  };
  textSelectionState.set(element, next);

  try {
    element.setSelectionRange?.(next.start, next.end);
  } catch {
    // Keep the HUD-side selection state for forwarded edits.
  }
}

function textMeasureContext() {
  if (!textMeasureCanvas) textMeasureCanvas = document.createElement("canvas");
  return textMeasureCanvas.getContext("2d");
}

function textControlIndexFromPoint(element, x, y) {
  const value = String(element.value || "");
  const rect = element.getBoundingClientRect();
  const style = window.getComputedStyle(element);
  const paddingLeft = Number.parseFloat(style.paddingLeft) || 0;
  const paddingTop = Number.parseFloat(style.paddingTop) || 0;
  const lineHeight = Number.parseFloat(style.lineHeight) || Number.parseFloat(style.fontSize) * 1.2 || 14;
  const ctx = textMeasureContext();
  if (!ctx) return value.length;
  ctx.font = `${style.fontStyle} ${style.fontVariant} ${style.fontWeight} ${style.fontSize} / ${style.lineHeight} ${style.fontFamily}`;

  if (element.tagName?.toLowerCase() === "textarea") {
    const lineIndex = Math.max(0, Math.floor((y - rect.top - paddingTop + element.scrollTop) / lineHeight));
    const lines = value.split("\n");
    const targetLine = Math.min(lineIndex, lines.length - 1);
    const lineStart = lines.slice(0, targetLine).reduce((total, line) => total + line.length + 1, 0);
    const localX = Math.max(0, x - rect.left - paddingLeft + element.scrollLeft);
    const line = lines[targetLine] || "";
    let best = 0;
    for (let index = 0; index <= line.length; index += 1) {
      const width = ctx.measureText(line.slice(0, index)).width;
      if (width <= localX) best = index;
      if (width > localX) break;
    }
    return lineStart + best;
  }

  const localX = Math.max(0, x - rect.left - paddingLeft + element.scrollLeft);
  let best = 0;
  for (let index = 0; index <= value.length; index += 1) {
    const width = ctx.measureText(value.slice(0, index)).width;
    if (width <= localX) best = index;
    if (width > localX) break;
  }
  return best;
}

function ensureTextCaret() {
  if (textCaretElement) return textCaretElement;
  textCaretElement = document.createElement("div");
  textCaretElement.className = "hud-text-caret";
  textCaretElement.setAttribute("aria-hidden", "true");
  document.body.appendChild(textCaretElement);
  return textCaretElement;
}

function hideTextCaret() {
  textCaretTarget = null;
  if (textCaretElement) textCaretElement.style.display = "none";
}

function blurActiveTextControl() {
  const element = currentTextControl();
  if (!element) {
    hideTextCaret();
    return;
  }

  try {
    element.blur();
  } catch {
    // Hiding the HUD-drawn caret is the important part if Chromium refuses blur.
  }
  hideTextCaret();
}

export function clearHudTextFocus() {
  activeTextSelectionDrag = null;
  activeTextareaResize = null;
  pendingClipboardPasteTarget = null;
  activePointerTarget = null;
  clearHudPointerFeedback();
  closeSelectPopup();
  hideTextCaret();

  if (isTextControl(document.activeElement) || document.activeElement?.tagName?.toLowerCase() === "select") {
    try {
      document.activeElement.blur();
    } catch {
      // Losing focus is best-effort; hiding the HUD-drawn caret is the important part.
    }
  }
}

function updateTextCaret(element = currentTextControl()) {
  if (!isTextControl(element) || document.activeElement !== element || !element.isConnected) {
    hideTextCaret();
    return;
  }

  const rect = element.getBoundingClientRect();
  if (!rect.width || !rect.height) {
    hideTextCaret();
    return;
  }

  const caret = ensureTextCaret();
  textCaretTarget = element;
  const style = window.getComputedStyle(element);
  const paddingLeft = Number.parseFloat(style.paddingLeft) || 0;
  const paddingTop = Number.parseFloat(style.paddingTop) || 0;
  const paddingBottom = Number.parseFloat(style.paddingBottom) || 0;
  const lineHeight = Number.parseFloat(style.lineHeight) || Number.parseFloat(style.fontSize) * 1.2 || 14;
  const value = String(element.value || "");
  const position = textSelection(element).start;
  const ctx = textMeasureContext();
  if (!ctx) return;
  ctx.font = `${style.fontStyle} ${style.fontVariant} ${style.fontWeight} ${style.fontSize} / ${style.lineHeight} ${style.fontFamily}`;

  let left = rect.left + paddingLeft - element.scrollLeft;
  let top = rect.top + paddingTop;

  if (element.tagName?.toLowerCase() === "textarea") {
    const before = value.slice(0, position);
    const lines = before.split("\n");
    const currentLine = lines[lines.length - 1] || "";
    left += ctx.measureText(currentLine).width;
    top += (lines.length - 1) * lineHeight - element.scrollTop;
  } else {
    left += ctx.measureText(value.slice(0, position)).width;
    top = rect.top + Math.max(2, (rect.height - lineHeight) / 2);
  }

  const height = Math.max(12, Math.min(lineHeight, rect.height - paddingTop - paddingBottom || lineHeight));
  caret.style.display = "block";
  caret.style.left = `${Math.max(rect.left + 2, Math.min(rect.right - 3, left))}px`;
  caret.style.top = `${Math.max(rect.top + 2, Math.min(rect.bottom - height - 2, top))}px`;
  caret.style.height = `${height}px`;
}

function startTextSelectionDrag(element, input) {
  const index = textControlIndexFromPoint(element, input.x, input.y);
  focusTarget(element);
  setTextSelection(element, index, index);
  activeTextSelectionDrag = { element, anchor: index };
  updateTextCaret(element);
}

function updateTextSelectionDrag(input) {
  if (!activeTextSelectionDrag) return false;
  const { element, anchor } = activeTextSelectionDrag;
  const index = textControlIndexFromPoint(element, input.x, input.y);
  setTextSelection(element, Math.min(anchor, index), Math.max(anchor, index));
  updateTextCaret(element);
  return true;
}

function stopTextSelectionDrag() {
  if (!activeTextSelectionDrag) return false;
  updateTextCaret(activeTextSelectionDrag.element);
  activeTextSelectionDrag = null;
  return true;
}

function updateTextareaResize(input) {
  if (!activeTextareaResize) return false;

  const { element, startY, startHeight, minHeight, maxHeight } = activeTextareaResize;
  const deltaY = Number(input.y) - startY;
  const nextHeight = Math.max(minHeight, Math.min(maxHeight, startHeight + deltaY));
  element.style.height = `${nextHeight}px`;
  updateTextCaret(element);
  return true;
}

function stopTextareaResize() {
  if (!activeTextareaResize) return false;
  updateTextCaret(activeTextareaResize.element);
  activeTextareaResize = null;
  return true;
}

function hasScrollableOverflow(element, axis) {
  if (!element) return false;
  const style = window.getComputedStyle(element);
  const overflow = axis === "y" ? style.overflowY : style.overflowX;
  if (!["auto", "scroll", "overlay"].includes(overflow)) return false;

  return axis === "y"
    ? element.scrollHeight > element.clientHeight + 1
    : element.scrollWidth > element.clientWidth + 1;
}

function canScrollByDelta(element, input) {
  if (!element) return false;
  const deltaY = Number(input?.deltaY || 0);
  const deltaX = Number(input?.deltaX || 0);

  if (deltaY < 0 && element.scrollTop > 0) return true;
  if (deltaY > 0 && element.scrollTop < element.scrollHeight - element.clientHeight - 1) return true;
  if (deltaX < 0 && element.scrollLeft > 0) return true;
  if (deltaX > 0 && element.scrollLeft < element.scrollWidth - element.clientWidth - 1) return true;

  return false;
}

function uniqueElements(elements) {
  return elements.filter((element, index) => element && elements.indexOf(element) === index);
}

function markedScrollableElementsAtPoint(input) {
  if (!input) return [];
  const x = Number(input.x);
  const y = Number(input.y);
  if (!Number.isFinite(x) || !Number.isFinite(y)) return [];

  return Array.from(document.querySelectorAll(HUD_SCROLL_SELECTOR))
    .filter((element) => {
      const rect = element.getBoundingClientRect();
      return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
    })
    .sort((a, b) => {
      const ar = a.getBoundingClientRect();
      const br = b.getBoundingClientRect();
      return ar.width * ar.height - br.width * br.height;
    });
}

function nearestScrollableElement(target, input = null) {
  const candidates = [];

  for (let element = target; element && element !== document.body; element = element.parentElement) {
    if (hasScrollableOverflow(element, "y") || hasScrollableOverflow(element, "x")) {
      candidates.push(element);
    }
  }

  candidates.push(...markedScrollableElementsAtPoint(input));

  const markedAncestor = target?.closest?.(HUD_SCROLL_SELECTOR);
  if (markedAncestor) candidates.unshift(markedAncestor);

  for (const element of uniqueElements(candidates)) {
    if ((hasScrollableOverflow(element, "y") || hasScrollableOverflow(element, "x")) && canScrollByDelta(element, input)) {
      return element;
    }
  }

  for (const element of uniqueElements(candidates)) {
    if (hasScrollableOverflow(element, "y") || hasScrollableOverflow(element, "x")) return element;
  }

  const documentScroller = document.scrollingElement;
  if (documentScroller && (hasScrollableOverflow(documentScroller, "y") || hasScrollableOverflow(documentScroller, "x"))) {
    return documentScroller;
  }

  return null;
}

function dispatchSyntheticWheel(target, input) {
  const event = new WheelEvent("wheel", {
    bubbles: true,
    cancelable: true,
    clientX: input.x,
    clientY: input.y,
    deltaY: input.deltaY || 0,
    view: window,
  });
  target.dispatchEvent(event);
  return event;
}

function scrollNearestElement(target, input) {
  const scrollable = nearestScrollableElement(target, input);
  if (!scrollable) return false;

  scrollable.scrollBy({
    top: input.deltaY || 0,
    left: input.deltaX || 0,
    behavior: "auto",
  });
  return true;
}

function scrollbarHitForPoint(target, input) {
  if (target?.closest?.(HUD_RESIZE_HANDLE_SELECTOR)) return null;
  if (textAreaResizeHitForPoint(target, input)) return null;

  const x = Number(input.x);
  const y = Number(input.y);
  const candidates = uniqueElements([
    nearestScrollableElement(target, input),
    ...markedScrollableElementsAtPoint(input),
  ]);

  for (const scrollable of candidates) {
    const rect = scrollable.getBoundingClientRect();
    const canScrollY = hasScrollableOverflow(scrollable, "y");
    const canScrollX = hasScrollableOverflow(scrollable, "x");
    const verticalGutter = Math.max(10, scrollable.offsetWidth - scrollable.clientWidth);
    const horizontalGutter = Math.max(10, scrollable.offsetHeight - scrollable.clientHeight);

    if (
      canScrollY &&
      x >= rect.right - verticalGutter - 2 &&
      x <= rect.right &&
      y >= rect.top &&
      y <= rect.bottom - (canScrollX ? horizontalGutter : 0)
    ) {
      return { axis: "y", element: scrollable };
    }

    if (
      canScrollX &&
      y >= rect.bottom - horizontalGutter - 2 &&
      y <= rect.bottom &&
      x >= rect.left &&
      x <= rect.right - (canScrollY ? verticalGutter : 0)
    ) {
      return { axis: "x", element: scrollable };
    }
  }

  return null;
}

function setScrollbarPositionFromPointer(scrollDrag, input) {
  const element = scrollDrag.element;
  const rect = element.getBoundingClientRect();

  if (scrollDrag.axis === "y") {
    const maxScroll = Math.max(0, element.scrollHeight - element.clientHeight);
    const trackLength = Math.max(1, rect.height);
    const ratio = Math.max(0, Math.min(1, (input.y - rect.top) / trackLength));
    element.scrollTop = ratio * maxScroll;
    return;
  }

  const maxScroll = Math.max(0, element.scrollWidth - element.clientWidth);
  const trackLength = Math.max(1, rect.width);
  const ratio = Math.max(0, Math.min(1, (input.x - rect.left) / trackLength));
  element.scrollLeft = ratio * maxScroll;
}

function shouldDropCoarsePointerMove(target) {
  if (!target?.closest?.(HUD_COARSE_POINTER_SELECTOR)) return false;

  const now = window.performance?.now?.() || Date.now();
  if (now - lastCoarsePointerMoveAt < COARSE_POINTER_MOVE_INTERVAL_MS) return true;

  lastCoarsePointerMoveAt = now;
  return false;
}

window.triffHudDispatchInput = (input) => {
  const type = input?.type;
  if (!type) return;

  if (type === "pointerleave") {
    activeTextareaResize = null;
    updateSyntheticHover(null, input);
    clearHudPointerFeedback();
    return;
  }

  if (type === "wheel") {
    const target = document.elementFromPoint(input.x, input.y) || document.body;
    if (activeSelectPopup?.popup.contains(target)) {
      activeSelectPopup.popup.scrollBy({ top: input.deltaY || 0, behavior: "auto" });
      return;
    }
    dispatchSyntheticWheel(target, input);
    scrollNearestElement(target, input);
    return;
  }

  const hitTarget = document.elementFromPoint(input.x, input.y) || document.body;

  if (type === "pointerdown") {
    updateSyntheticHover(hitTarget, input);
    const interactiveTarget = interactiveElementFromTarget(hitTarget);
    setHudHover(interactiveTarget);
    setHudPressed(interactiveTarget);

    const popupHit = hitTarget?.closest?.("[data-hud-select-popup='true']");
    const hitSelect = selectControlFromTarget(hitTarget);
    if (activeSelectPopup && !popupHit && hitSelect !== activeSelectPopup.select) {
      closeSelectPopup();
    }

    if (hitSelect) {
      if (activeSelectPopup?.select === hitSelect) {
        closeSelectPopup();
      } else {
        openSelectPopup(hitSelect);
      }
      setHudPressed(null);
      suppressNextPointerUp = true;
      return;
    }

    if (!isTextControl(hitTarget)) {
      blurActiveTextControl();
    }

    const textareaResizeHit = textAreaResizeHitForPoint(hitTarget, input);
    if (textareaResizeHit) {
      activeTextareaResize = textareaResizeHit;
      activePointerTarget = null;
      activePointerButtons = 0;
      setHudPressed(null);
      focusTarget(textareaResizeHit.element);
      updateTextCaret(textareaResizeHit.element);
      return;
    }

    if (!hitTarget?.closest?.(HUD_RESIZE_HANDLE_SELECTOR)) {
      const scrollHit = scrollbarHitForPoint(hitTarget, input);
      if (scrollHit) {
        activeScrollDrag = scrollHit;
        setScrollbarPositionFromPointer(activeScrollDrag, input);
        return;
      }
    }

    activePointerTarget = hitTarget;
    activePointerButtons = buttonsForDomButton(input.button || 0);
    focusTarget(hitTarget);
    maybeSelectOnFocus(hitTarget);
    if (isTextControl(hitTarget)) {
      startTextSelectionDrag(hitTarget, input);
    }
    dispatchPointerEvent(hitTarget, "pointerdown", { ...input, buttons: activePointerButtons });
    dispatchMouseLikeEvent(hitTarget, "mousedown", { ...input, buttons: activePointerButtons });
    maybeSelectOnDoubleClick(hitTarget, input);

    if (input.button === 2 && isTextControl(hitTarget)) {
      requestClipboardPaste(hitTarget);
    }
    return;
  }

  if (type === "pointermove") {
    if (updateTextareaResize(input)) return;

    if (updateTextSelectionDrag(input)) return;

    if (activeScrollDrag) {
      setScrollbarPositionFromPointer(activeScrollDrag, input);
      return;
    }

    if (!activePointerTarget) {
      updateSyntheticHover(hitTarget, input);
      setHudHover(interactiveElementFromTarget(hitTarget));
    }

    const moveTarget = activePointerTarget ? window : hitTarget;
    if (!activePointerTarget && shouldDropCoarsePointerMove(moveTarget)) return;

    dispatchPointerEvent(moveTarget, "pointermove", { ...input, buttons: activePointerTarget ? activePointerButtons : 0 });
    dispatchMouseLikeEvent(moveTarget, "mousemove", { ...input, buttons: activePointerTarget ? activePointerButtons : 0 });
    return;
  }

  if (type === "pointerup") {
    if (stopTextareaResize()) {
      activePointerTarget = null;
      activePointerButtons = 0;
      setHudPressed(null);
      setHudHover(interactiveElementFromTarget(hitTarget));
      return;
    }

    if (stopTextSelectionDrag()) {
      activePointerTarget = null;
      activePointerButtons = 0;
      setHudPressed(null);
      setHudHover(interactiveElementFromTarget(hitTarget));
      return;
    }

    if (suppressNextPointerUp) {
      suppressNextPointerUp = false;
      activePointerTarget = null;
      activePointerButtons = 0;
      setHudPressed(null);
      setHudHover(interactiveElementFromTarget(hitTarget));
      return;
    }

    if (activeScrollDrag) {
      activeScrollDrag = null;
      setHudPressed(null);
      return;
    }

    const upTarget = activePointerTarget || hitTarget;
    dispatchPointerEvent(window, "pointerup", input);
    dispatchMouseLikeEvent(window, "mouseup", input);
    if (input.button === 2) {
      dispatchMouseLikeEvent(upTarget, "contextmenu", { ...input, buttons: 0 });
    } else if ((input.button || 0) === 0) {
      dispatchMouseLikeEvent(upTarget, "click", input);
      pulseHudClick(interactiveElementFromTarget(upTarget));
    }
    if ((input.button || 0) === 0 && Number(input.clickCount || 0) >= 2) {
      dispatchMouseLikeEvent(upTarget, "dblclick", input);
    }
    activePointerTarget = null;
    activePointerButtons = 0;
    setHudPressed(null);
    setHudHover(interactiveElementFromTarget(hitTarget));
  }
};

function activeTextControl() {
  return currentTextControl();
}

function emitInput(element, inputType = "insertText", data = null) {
  try {
    element.dispatchEvent(new InputEvent("beforeinput", { bubbles: true, cancelable: true, inputType, data }));
  } catch {
    element.dispatchEvent(new Event("beforeinput", { bubbles: true, cancelable: true }));
  }

  element.dispatchEvent(new Event("input", { bubbles: true }));
  element.dispatchEvent(new Event("change", { bubbles: true }));
  updateTextCaret(element);
}

function setControlValue(element, value) {
  const descriptor = Object.getOwnPropertyDescriptor(element.constructor.prototype, "value");
  if (descriptor?.set) descriptor.set.call(element, value);
  else element.value = value;
}

function replaceSelection(element, text) {
  const { start, end } = textSelection(element);
  const maxLength = Number(element.getAttribute("maxLength") || element.maxLength || -1);
  const before = element.value.slice(0, start);
  const after = element.value.slice(end);
  let next = `${before}${text}${after}`;
  let cursor = before.length + text.length;

  if (maxLength >= 0 && next.length > maxLength) {
    next = next.slice(0, maxLength);
    cursor = Math.min(cursor, maxLength);
  }

  setControlValue(element, next);
  setTextSelection(element, cursor, cursor);
  emitInput(element, text ? "insertText" : "deleteContentBackward", text);
}

function commitActiveTextControl() {
  const element = currentTextControl();
  if (element) emitInput(element, "insertReplacementText", null);
}

async function requestClipboardPaste(element) {
  if (!isTextControl(element)) return;
  focusTarget(element);
  pendingClipboardPasteTarget = element;

  if (postNative({ type: "read-clipboard" })) return;

  try {
    const text = await navigator.clipboard.readText();
    if (pendingClipboardPasteTarget === element) {
      if (text) replaceSelection(element, text);
      pendingClipboardPasteTarget = null;
    }
  } catch {
    pendingClipboardPasteTarget = null;
  }
}

function copyTextFromControl(element, cut = false) {
  if (!isTextControl(element)) return;
  const { start, end } = textSelection(element);
  if (start === end) return;

  const text = element.value.slice(start, end);
  if (!text) return;

  if (!postNative({ type: "copy-text", text })) {
    try {
      void navigator.clipboard?.writeText?.(text);
    } catch {
      // Native clipboard is the normal path inside TriffHUD.
    }
  }

  if (cut && !element.readOnly && !element.disabled) {
    replaceSelection(element, "");
  }
}

window.chrome?.webview?.addEventListener?.("message", (event) => {
  const message = event.data;
  if (message?.type === "clipboard" && pendingClipboardPasteTarget) {
    const target = pendingClipboardPasteTarget;
    const text = message.text || "";
    pendingClipboardPasteTarget = null;
    if (text) replaceSelection(target, text);
  }

  if (
    (message?.type === "visibility" && message.visible === false) ||
    (message?.type === "click-through" && message.enabled === true)
  ) {
    clearHudTextFocus();
  }

  for (const listener of listeners) listener(message);
});

window.triffHudDispatchText = (input) => {
  const element = activeTextControl();
  if (!element || !input?.text) return;
  replaceSelection(element, input.text);
};

window.triffHudDispatchKey = (input) => {
  try {
    const event = new CustomEvent("triff:hud-keydown", {
      cancelable: true,
      detail: input,
    });
    if (!window.dispatchEvent(event)) return;
  } catch {
    // Keep legacy key forwarding working if CustomEvent construction fails.
  }

  const element = activeTextControl();
  const key = input?.key;
  if (!key) return;

  if (activeSelectPopup && key === "Escape") {
    closeSelectPopup();
    return;
  }

  if (!element) {
    if (key === "Escape") document.activeElement?.blur?.();
    return;
  }

  const { start, end } = textSelection(element);

  if (key === "Enter") {
    if (element.tagName?.toLowerCase() === "textarea") {
      replaceSelection(element, "\n");
      return;
    }

    element.form?.requestSubmit?.();
    return;
  }

  if (key === "Escape") {
    element.blur();
    return;
  }

  if (key === "SelectAll") {
    selectTextControl(element);
    updateTextCaret(element);
    return;
  }

  if (key === "Copy") {
    copyTextFromControl(element, false);
    return;
  }

  if (key === "Cut") {
    copyTextFromControl(element, true);
    return;
  }

  if (key === "Paste") {
    void requestClipboardPaste(element);
    return;
  }

  if (key === "Backspace") {
    if (start !== end) replaceSelection(element, "");
    else if (start > 0) {
      setControlValue(element, element.value.slice(0, start - 1) + element.value.slice(end));
      setTextSelection(element, start - 1, start - 1);
      emitInput(element, "deleteContentBackward", null);
    }
    return;
  }

  if (key === "Delete") {
    if (start !== end) replaceSelection(element, "");
    else if (start < element.value.length) {
      setControlValue(element, element.value.slice(0, start) + element.value.slice(start + 1));
      setTextSelection(element, start, start);
      emitInput(element, "deleteContentForward", null);
    }
    return;
  }

  if (key === "ArrowLeft") {
    const cursor = Math.max(0, start - 1);
    setTextSelection(element, cursor, cursor);
    updateTextCaret(element);
    return;
  }

  if (key === "ArrowRight") {
    const cursor = Math.min(element.value.length, end + 1);
    setTextSelection(element, cursor, cursor);
    updateTextCaret(element);
  }
};

document.addEventListener("focusin", (event) => {
  if (isTextControl(event.target)) {
    window.requestAnimationFrame(() => updateTextCaret(event.target));
  }
});

document.addEventListener("focusout", (event) => {
  if (isTextControl(event.target)) {
    hideTextCaret();
  }
});

document.addEventListener("selectionchange", () => {
  updateTextCaret();
});

const textCaretCleanupObserver = new MutationObserver(() => {
  if (textCaretTarget && !textCaretTarget.isConnected) hideTextCaret();
});
textCaretCleanupObserver.observe(document.documentElement, {
  childList: true,
  subtree: true,
});

document.addEventListener("scroll", () => updateTextCaret(), true);
window.addEventListener("resize", () => updateTextCaret());

window.triffHud = {
  postNative,
  setClickThrough: setNativeClickThrough,
  toggleClickThrough: toggleNativeClickThrough,
  hide: hideHud,
  clearTextFocus: clearHudTextFocus,
};
