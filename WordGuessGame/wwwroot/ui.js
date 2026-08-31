import { state } from "./state.js";
import * as dom from "./dom.js";
import { escapeHtml, formatThousands } from "./utils.js";

const WINNERS_HISTORY_VISIBLE_COUNT = 5;

function updateExpandButton(listEl, btnEl, visibleCount) {
    if (!listEl || !btnEl) return;
    const total = listEl.children.length;
    if (total <= visibleCount) {
        listEl.classList.remove("expanded");
        btnEl.style.display = "none";
        return;
    }
    btnEl.style.display = "block";
    const expanded = listEl.classList.contains("expanded");
    btnEl.textContent = expanded ? "Show less" : `Show more (${total - visibleCount} more)`;
}

export function updateWinnersHistoryExpandButton() {
    updateExpandButton(dom.winnersHistoryList, dom.winnersHistoryExpandBtn, WINNERS_HISTORY_VISIBLE_COUNT);
}

export function toggleWinnersHistoryExpand() {
    if (!dom.winnersHistoryList) return;
    dom.winnersHistoryList.classList.toggle("expanded");
    updateWinnersHistoryExpandButton();
}

function formatWinnerDate(dateStr) {
    const d = new Date(dateStr);
    if (isNaN(d.getTime())) return "";
    return d.toLocaleString(undefined, { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

export async function loadWinnersHistory() {
    if (!dom.winnersHistoryList) return;
    try {
        const res = await fetch("winners/history");
        if (!res.ok) throw new Error(`winners/history ${res.status}`);
        const items = await res.json();
        dom.winnersHistoryList.innerHTML = "";
        (Array.isArray(items) ? items : []).forEach(item => {
            const li = document.createElement("li");
            const dateStr = formatWinnerDate(item.date);
            li.innerHTML = `<span class="user">${escapeHtml(item.player)}</span>` +
                (dateStr ? ` <span class="guess">${escapeHtml(dateStr)}</span>` : "");
            dom.winnersHistoryList.appendChild(li);
        });
        updateWinnersHistoryExpandButton();
    } catch (e) {
        console.error("Failed to load winners history:", e);
    }
}

export function hasSelectedName() {
    return !!(dom.userNameInput.value && dom.userNameInput.value.trim().length);
}

export function getUser() {
    const name = (dom.userNameInput.value || "").trim();
    if (name.length) {
        state.cachedUser = name;
        return name;
    }
    if (!state.cachedUser) {
        state.cachedUser = `User-${Math.floor(Math.random() * 10000)}`;
    }
    return state.cachedUser;
}

export function flashButtonDone(btn, duration = 1000) {
    if (!btn) return;
    if (!btn.dataset.originalText) btn.dataset.originalText = btn.textContent;
    if (btn._flashTimeout) clearTimeout(btn._flashTimeout);
    btn.textContent = "Done!";
    btn.classList.add("btn-flash-success");
    btn._flashTimeout = setTimeout(() => {
        btn.textContent = btn.dataset.originalText;
        btn.classList.remove("btn-flash-success");
        btn._flashTimeout = null;
    }, duration);
}

// Colors the status line green (with a check icon) for wins/success, or yellow
// (with a "!" icon, matching the results-hint banner) for warnings/errors.
export function setStatus(text, kind = null) {
    dom.statusText.textContent = text;
    if (dom.statusSection) {
        dom.statusSection.classList.remove("success", "warning");
        if (kind) dom.statusSection.classList.add(kind);
    }
}

export function updateWordCount(count) {
    if (!dom.wordCountDisplay || !dom.wordCountValue) return;
    dom.wordCountValue.textContent = count > 0 ? (count === 1 ? "1 word" : `${count} words`) : "0";
}

export function applyCanvasEnablement() {
    const someoneIsPainter = !!state.currentPainter;
    const iAmGlobalPainter = someoneIsPainter && state.currentPainter === getUser();
    const disableCanvas = state.isGameOver || !state.hasAnswer || state.triviaMode;
    if (dom.paintCanvas) {
        dom.paintCanvas.classList.toggle("disabled", disableCanvas);
    }
    if (dom.paintControls) {
        dom.paintControls.style.display = (!disableCanvas && iAmGlobalPainter) ? "flex" : "none";
    }
}

export function applyGameMode() {
    if (dom.gameModeToggle) dom.gameModeToggle.classList.toggle("trivia", state.triviaMode);
    if (dom.gameModeDrawingBtn) dom.gameModeDrawingBtn.classList.toggle("active", !state.triviaMode);
    if (dom.gameModeTriviaBtn) dom.gameModeTriviaBtn.classList.toggle("active", state.triviaMode);
    const newTitle = state.triviaMode ? "Trivia 2" : "Skribbl 2";
    document.title = newTitle;
    const h1 = document.querySelector("header h1");
    if (h1) h1.textContent = newTitle;
    if (state.triviaMode) {
        if (dom.painterSection) dom.painterSection.style.display = "none";
        if (dom.paintSection) dom.paintSection.style.display = "none";
        if (dom.guessSection) dom.guessSection.style.display = "none";
        if (dom.triviaQuestionSection) dom.triviaQuestionSection.style.display = state.isPainter ? "block" : "none";
        if (dom.triviaGuessSection) dom.triviaGuessSection.style.display = state.isPainter ? "none" : "block";
    } else {
        if (dom.triviaQuestionSection) dom.triviaQuestionSection.style.display = "none";
        if (dom.triviaGuessSection) dom.triviaGuessSection.style.display = "none";
    }
}

export function applyTriviaGuessState() {
    if (!dom.triviaGuessInput || !dom.triviaGuessBtn) return;
    const canAnswer = state.triviaMode
        && !state.isPainter
        && hasSelectedName()
        && !!state.triviaQuestionText
        && !state.myTriviaAnswerSubmitted
        && !state.isGameOver;
    dom.triviaGuessInput.disabled = !canAnswer;
    dom.triviaGuessBtn.disabled = !canAnswer;
}

export function updatePainterUI() {
    dom.painterBtn.classList.toggle("active", state.isPainter);
    dom.painterBtn.setAttribute("aria-pressed", String(state.isPainter));
    dom.painterBtn.textContent = state.isPainter ? "Hosting..." : "Become a host";
    if (dom.gameModeToggle) dom.gameModeToggle.style.display = state.isPainter ? "inline-flex" : "none";
    dom.managePlayersSection.style.display = state.isPainter ? "block" : "none";
    dom.setTopicBtn.style.display = state.isPainter ? "inline-block" : "none";
    dom.topicInput.style.display = state.isPainter ? "block" : "none";
}

export function applyNameRowVisibility() {
    if (dom.userNameRow) dom.userNameRow.style.display = "flex";
    if (dom.userNameInput) dom.userNameInput.style.display = state.isPainter ? "none" : "block";
    if (dom.painterBtn) dom.painterBtn.style.display = "inline-flex";
}

export function applyGlobalPainterVisibility() {
    const me = getUser();
    const someoneIsPainter = !!state.currentPainter;
    const iAmGlobalPainter = someoneIsPainter && state.currentPainter === me;
    dom.resetSection.style.display = iAmGlobalPainter ? "block" : "none";
    if (dom.paintSection && !state.triviaMode) dom.paintSection.style.display = "block";
    if (dom.paintControls) dom.paintControls.style.display = (iAmGlobalPainter && state.hasAnswer && !state.isGameOver && !state.triviaMode) ? "flex" : "none";
    applyCanvasEnablement();
}

export function setInputsEnabled(enabled) {
    const nameSelected = hasSelectedName();
    if (!state.triviaMode) {
        dom.painterSection.style.display = state.isPainter ? "block" : "none";
        dom.guessSection.style.display = state.isPainter ? "none" : "block";
    }
    applyNameRowVisibility();
    dom.answerInput.disabled = !enabled || state.isGameOver || !state.isPainter;
    dom.setAnswerBtn.disabled = !enabled || state.isGameOver || !state.isPainter || state.hasAnswer;
    const canGuess = enabled && !state.isGameOver && !state.isPainter && nameSelected;
    dom.guessInput.disabled = !canGuess;
    dom.guessBtn.disabled = !canGuess;
    dom.userNameInput.disabled = !enabled || state.isGameOver;
    if (!state.isPainter && !nameSelected && !state.isGameOver && enabled) {
        setStatus("Select your name to start guessing.");
    }
    dom.painterBtn.style.display = "inline-flex";
    updatePainterUI();
    applyGlobalPainterVisibility();
    applyGameMode();
    applyTriviaGuessState();
}

export async function populateNames() {
    try {
        const res = await fetch("players");
        if (!res.ok) throw new Error(`players ${res.status}`);
        const names = await res.json();
        dom.userNameInput.innerHTML = "";
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.disabled = true;
        placeholder.selected = true;
        placeholder.textContent = "Select your name";
        dom.userNameInput.appendChild(placeholder);
        names.forEach(n => {
            const opt = document.createElement("option");
            opt.value = n;
            opt.textContent = n;
            dom.userNameInput.appendChild(opt);
        });
    } catch (e) {
        console.error("Failed to load players:", e);
        setStatus("Failed to load players.", "warning");
    }
}

export async function loadAndRenderResultsFromFile(animateWinner = false) {
    try {
        const res = await fetch("results");
        if (!res.ok) throw new Error(`results ${res.status}`);
        const items = await res.json();
        renderResults(items, animateWinner);
    } catch (e) {
        console.error("Failed to load results:", e);
        setStatus("Failed to load results.", "warning");
    }
}

export async function loadTopic() {
    try {
        const res = await fetch("topic");
        if (!res.ok) throw new Error(`topic ${res.status}`);
        const payload = await res.json();
        const t = (payload && payload.topic) ? String(payload.topic) : "";
        renderTopic(t);
    } catch (e) {
        console.error("Failed to load topic:", e);
    }
}

export function renderTopic(topic) {
    if (!dom.topicLabel || !dom.topicValue) return;
    dom.topicLabel.textContent = "Current topic:";
    dom.topicLabel.style.color = "white";
    dom.topicValue.textContent = String(topic || "").toUpperCase();
    dom.topicValue.style.color = "#22c55e";
    dom.topicValue.style.fontWeight = "bold";
    dom.topicValue.style.textAlign = "center";
}

export function renderResults(items, animateWinner = false) {
    dom.resultsBody.innerHTML = "";
    if (!Array.isArray(items) || items.length === 0) {
        updateResetHint(items);
        return;
    }
    const activeSet = new Set((state.activePlayers || []).map(a => a.toLowerCase()));
    items.forEach(x => {
        const crown = x.isLastWinner ? " \uD83D\uDC51" : "";
        const isActive = activeSet.has(String(x.name).toLowerCase());
        const nameCell = isActive ? `<span class="active-player">${escapeHtml(x.name)}</span>${crown}` : `${escapeHtml(x.name)}${crown}`;
        const tr = document.createElement("tr");
        if (animateWinner && x.isLastWinner) tr.classList.add("winner-flash");
        tr.innerHTML = `<td>${nameCell}</td><td>${x.points}</td>`;
        dom.resultsBody.appendChild(tr);
    });
    updateResetHint(items);
}

// Nudges players to start a fresh round (Reset Game) once someone has racked up
// a lot of points and it's still early in the week, so scores don't snowball all week.
function updateResetHint(items) {
    if (!dom.resetHint || !dom.resetHintText) return;
    const dayOfWeek = new Date().getDay(); // 0=Sun, 1=Mon, 2=Tue, ...
    const isStartOfNewSprint = dayOfWeek === 1 || dayOfWeek === 2 || dayOfWeek === 3;
    const maxPoints = Array.isArray(items) ? items.reduce((m, x) => Math.max(m, Number(x.points) || 0), 0) : 0;
    const shouldShow = isStartOfNewSprint && maxPoints >= 8;
    dom.resetHint.style.display = shouldShow ? "flex" : "none";
    if (shouldShow) {
        dom.resetHintText.textContent = "Please consider whether the result should be reset.";
    }
}

export function updateStatus(serverState) {
    state.isGameOver = !!serverState.isGameOver;
    state.hasAnswer = !!serverState.hasAnswer;
    state.lastWinner = serverState.lastWinner ? String(serverState.lastWinner).trim() : "";
    if (serverState.gameMode !== undefined) {
        state.triviaMode = serverState.gameMode === "trivia";
    }
    if (serverState.triviaQuestion) {
        state.triviaQuestionText = serverState.triviaQuestion;
        if (dom.triviaQuestionDisplay) dom.triviaQuestionDisplay.value = state.triviaQuestionText;
        applyTriviaGuessState();
    }
    if (state.triviaMode && Array.isArray(serverState.triviaAnswers) && serverState.triviaAnswers.length > 0 && dom.historyList.children.length === 0) {
        serverState.triviaAnswers.forEach(item => {
            const li = document.createElement("li");
            li.innerHTML = `<span class="user">${escapeHtml(item.user)}</span>: <span class="guess">${escapeHtml(item.answer)}</span>`;
            dom.historyList.appendChild(li);
        });
        const me = getUser();
        if (me && serverState.triviaAnswers.some(a => a.user === me)) {
            state.myTriviaAnswerSubmitted = true;
        }
    }
    const someoneIsPainter = !!state.currentPainter;
    const iAmGlobalPainter = someoneIsPainter && state.currentPainter === getUser();
    const nameSelected = hasSelectedName();
    if (!state.triviaMode) {
        if (!serverState.hasAnswer && !state.isGameOver) {
            setStatus("Waiting for answer...");
        } else if (serverState.hasAnswer && !state.isGameOver) {
            setStatus(iAmGlobalPainter
                ? "Answer set. You can start drawing!"
                : (nameSelected ? "Answer set. Keep guessing!" : "Answer set. Please select your name and start guessing!"));
        } else {
            const lw = serverState.lastWinner ? String(serverState.lastWinner).trim() : "";
            setStatus(lw ? `Congratulations, ${escapeHtml(lw)}!` : "Please reset the game!", "success");
        }
    } else if (!state.triviaQuestionText) {
        setStatus(state.isPainter ? "Trivia mode. Set a question!" : "Trivia mode. Waiting for question...");
    }
    const t = serverState.topic ? String(serverState.topic) : "";
    renderTopic(t);
    dom.topicInput.value = t;
    if (!state.triviaMode) {
        if (serverState.hasAnswer && serverState.answerWordCount > 0) updateWordCount(serverState.answerWordCount);
        else if (!serverState.hasAnswer) updateWordCount(0);
    }
    setInputsEnabled(!state.isGameOver);
    applyCanvasEnablement();
}

export function initPaletteAndTools() {
    if (dom.colorPalette && dom.paintColor) {
        dom.colorPalette.querySelectorAll(".color-swatch").forEach(btn => {
            btn.addEventListener("click", () => {
                const c = btn.getAttribute("data-color");
                if (c) dom.paintColor.value = c;
            });
        });
    }
    if (dom.toolButtons) {
        const buttons = Array.from(dom.toolButtons.querySelectorAll(".icon-btn"));
        const setActive = (tool) => {
            state.currentTool = tool;
            buttons.forEach(b => b.classList.toggle("active", b.getAttribute("data-tool") === tool));
        };
        setActive("freehand");
        dom.toolButtons.addEventListener("click", (e) => {
            const target = e.target.closest(".icon-btn");
            if (!target) return;
            const tool = target.getAttribute("data-tool");
            if (!tool) return;
            if (dom.paintTool) dom.paintTool.value = tool;
            setActive(tool);
        });
    }
}
