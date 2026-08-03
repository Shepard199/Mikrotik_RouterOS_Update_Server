const API_BASE = "/api";
let autoRefreshInterval = null;
let scheduleUpdateInterval = null;

// Интервалы обновления (в миллисекундах)
const INTERVALS = {
    STATUS: 5000, // /api/status каждые 5 сек
    VERSIONS: 60000 // /api/versions каждые 60 сек (или по событию)
};
const CLIENT_ACTIVITY_REFRESH_MS = 60000;
const TLS_DIAGNOSTICS_REFRESH_MS = 30000;
const POINTER_ROUTING_REFRESH_MS = 60000;
const LOG_STATS_TIMEOUT_MS = 10000;

// Хранилище таймеров
let timers = {
    status: null,
    versions: null
};
let lastClientActivityRefresh = 0;
let lastTlsDiagnosticsRefresh = 0;
let lastPointerRoutingRefresh = 0;
let cachedTlsProbe = null;

const MAIN_TABS = ["dashboard", "versions", "logs", "schedule", "changelog", "config"];

const TAB_LABELS = {
    dashboard: "📊 Dashboard",
    versions: "📦 Versions",
    logs: "📋 Logs",
    schedule: "📅 Schedule",
    changelog: "📝 Changelog",
    config: "⚙️ Configuration"
};

function refreshTabLabels() {
    TAB_LABELS.dashboard = `📊 ${t("dashboard")}`;
    TAB_LABELS.versions = `📦 ${t("versions")}`;
    TAB_LABELS.logs = `📋 ${t("logs")}`;
    TAB_LABELS.schedule = `📅 ${t("schedule")}`;
    TAB_LABELS.changelog = `📝 ${t("changelog")}`;
    TAB_LABELS.config = `⚙️ ${t("configuration")}`;
}

// ==================== LOCALIZATION SYSTEM ====================
let currentLanguage = "en";
let translations = {};
let availableLocales = [];

async function initLocalization() {
    try {
        // Загружаем текущий язык из бэкенда
        const langResponse = await fetch(`${API_BASE}/settings/language`);
        if (langResponse.ok) {
            const data = await langResponse.json();
            currentLanguage = data.language || "en";
            localStorage.setItem("uiLanguage", currentLanguage);
        } else {
            currentLanguage = localStorage.getItem("uiLanguage") || "en";
        }

        // Загружаем доступные локали
        const localesResponse = await fetch(`${API_BASE}/locales`);
        if (localesResponse.ok) {
            availableLocales = await localesResponse.json();
            populateLanguageSelector();
        }

        // Загружаем переводы
        await loadLanguage(currentLanguage);
        populateLanguageSelector();
    } catch (err) {
        console.error("Error initializing localization:", err);
        currentLanguage = "en";
        availableLocales = Array.from(new Set([localStorage.getItem("uiLanguage") || "en", "en"]));
        await loadLanguage("en");
        populateLanguageSelector();
    }
}

function populateLanguageSelector() {
    const selector = document.getElementById("language-select");
    if (!selector) return;

    // Сохраняем текущий выбор
    const currentValue = selector.value;

    // Очищаем селектор
    selector.innerHTML = "";

    const fallbackLanguageNames = {
        en: "English",
        ru: "Русский",
        es: "Español",
        fr: "Français",
        de: "Deutsch",
        it: "Italiano",
        pt: "Português",
        ja: "日本語",
        zh: "中文",
        ko: "한국어"
    };

    // Добавляем доступные языки
    availableLocales.forEach(lang => {
        const option = document.createElement("option");
        option.value = lang;
        const nameKey = `languageName_${lang}`;
        const translatedName = t(nameKey);
        option.textContent = translatedName !== nameKey
            ? translatedName
            : (fallbackLanguageNames[lang] || lang.toUpperCase());
        selector.appendChild(option);
    });

    // Восстанавливаем текущий выбор
    selector.value = currentValue || currentLanguage;
}

async function loadLanguage(lang) {
    try {
        const response = await fetch(`/lang/${lang}.json`);
        if (response.ok) {
            translations = await response.json();
            currentLanguage = lang;
            localStorage.setItem("uiLanguage", lang);
            populateLanguageSelector();
        } else {
            console.warn(`Language file not found: ${lang}, falling back to en`);
            const fallback = await fetch("/lang/en.json");
            translations = await fallback.json();
            currentLanguage = "en";
            localStorage.setItem("uiLanguage", "en");
            populateLanguageSelector();
        }
    } catch (err) {
        console.error(`Error loading language ${lang}:`, err);
    }
}

function t(key) {
    return translations[key] || key;
}

function translateOrFallback(key, fallback) {
    const value = t(key);
    return value === key ? fallback : value;
}

function safeText(value) {
    if (value === null || value === undefined) return "";
    return String(value);
}

async function fetchWithTimeout(url, options = {}, timeoutMs = 10000) {
    let timeout = null;
    const request = fetch(url, options);
    request.catch(() => {
        // Avoid unhandled rejections when the soft timeout wins the race.
    });
    const deadline = new Promise((_, reject) => {
        timeout = window.setTimeout(() => {
            const error = new Error("Request timed out");
            error.name = "TimeoutError";
            reject(error);
        }, timeoutMs);
    });

    try {
        return await Promise.race([request, deadline]);
    } finally {
        if (timeout !== null) {
            window.clearTimeout(timeout);
        }
    }
}

async function changeLanguage(lang) {
    try {
        // Сохраняем язык на бэкенде
        const response = await fetch(`${API_BASE}/settings/language`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(lang)
        });

        if (response.ok) {
            await loadLanguage(lang);
            applyTranslations();
            loadPointerRouting(true);
            await loadConsoleLogSettings();
            showToast(`${t("languageChangedTo")} ${lang.toUpperCase()}`, "success");
        } else {
            showToast(t("languageChangeError"), "error");
        }
    } catch (err) {
        console.error("Error changing language:", err);
        showToast(`${t("error")}: ${err.message}`, "error");
    }
}

function applyTranslations() {
    refreshTabLabels();
    populateLanguageSelector();

    // Обновляем селектор языка
    const langSelect = document.getElementById("language-select");
    if (langSelect) {
        langSelect.value = currentLanguage;
    }

    // Применяем переводы ко всем элементам с атрибутом data-i18n
    document.querySelectorAll("[data-i18n]").forEach(el => {
        const key = el.getAttribute("data-i18n");
        el.textContent = t(key);
    });

    // Применяем переводы к placeholder атрибутам
    document.querySelectorAll("[data-i18n-placeholder]").forEach(el => {
        const key = el.getAttribute("data-i18n-placeholder");
        el.placeholder = t(key);
    });

    // Применяем переводы к title атрибутам
    document.querySelectorAll("[data-i18n-title]").forEach(el => {
        const key = el.getAttribute("data-i18n-title");
        el.title = t(key);
    });

    // Применяем переводы к aria-label атрибутам
    document.querySelectorAll("[data-i18n-aria-label]").forEach(el => {
        const key = el.getAttribute("data-i18n-aria-label");
        el.setAttribute("aria-label", t(key));
    });

    const htmlRoot = document.documentElement;
    if (htmlRoot) {
        htmlRoot.lang = currentLanguage || "en";
    }

    document.title = t("appTitle");

    const theme = localStorage.getItem("theme") || "dark";
    updateThemeToggleUi(theme);
    refreshSelectedVersionsCount();
}

// ==================== TOAST NOTIFICATIONS SYSTEM ====================
/**
 * Show toast notification (success, error, warning, info)
 * @param {string} message - Message to display
 * @param {string} type - Type: 'success', 'error', 'warning', 'info'
 * @param {number} duration - Duration in ms (default 3000)
 */
function showToast(message, type = "info", duration = 3000) {
    const container = document.getElementById("toast-container");
    if (!container) return;

    const toast = document.createElement("div");

    toast.className = `toast toast-${type}`;
    toast.textContent = safeText(message);

    container.appendChild(toast);

    setTimeout(() => {
            toast.classList.add("removing");
            setTimeout(() => toast.remove(), 300);
        },
        duration);
}

// ==================== LOADING STATES ====================
/**
 * Show loading spinner in element
 * @param {string} elementId - Element ID
 * @param {string} text - Loading text (optional)
 */
function showLoading(elementId, text = null) {
    const el = document.getElementById(elementId);
    if (!el) return;
    const loadingMessage = text || t("loading");

    el.innerHTML = "";

    if (el.tagName === "TBODY") {
        const table = el.closest("table");
        const colSpan = table?.querySelectorAll("thead th").length || 1;

        const row = document.createElement("tr");
        const cell = document.createElement("td");
        cell.colSpan = colSpan;

        const loadingText = document.createElement("div");
        loadingText.className = "loading-text";
        loadingText.textContent = safeText(loadingMessage);

        const wrapper = document.createElement("div");
        wrapper.className = "loading-container";
        const spinner = document.createElement("div");
        spinner.className = "spinner";
        wrapper.appendChild(spinner);
        wrapper.appendChild(loadingText);
        cell.appendChild(wrapper);
        row.appendChild(cell);
        el.appendChild(row);
        return;
    }

    const container = document.createElement("div");
    container.className = "loading-container";

    const spinner = document.createElement("div");
    spinner.className = "spinner";
    container.appendChild(spinner);

    const loadingText = document.createElement("div");
    loadingText.className = "loading-text";
    loadingText.textContent = safeText(loadingMessage);
    container.appendChild(loadingText);

    el.appendChild(container);
}

/**
 * Show skeleton loader for content
 * @param {string} elementId - Element ID
 * @param {number} lines - Number of skeleton lines
 */
function showSkeletonLoader(elementId, lines = 3) {
    const el = document.getElementById(elementId);
    let html = '<div class="skeleton-loader">';
    for (let i = 0; i < lines; i++) {
        html += '<div class="skeleton-line skeleton"></div>';
    }
    html += "</div>";
    el.innerHTML = html;
}

// ==================== COPY TO CLIPBOARD ====================
/**
 * Copy code to clipboard
 * @param {string} elementId - Element ID containing text
 */
function copyCode(elementId) {
    const element = document.getElementById(elementId);
    const text = element.textContent;

    navigator.clipboard.writeText(text).then(() => {
        showToast(t("copiedToClipboard"), "success");
    }).catch(() => {
        showToast(t("copyFailed"), "error");
    });
}

// ==================== FORM VALIDATION ====================
/**
 * Validate form fields
 * @param {string} formId - Form ID to validate
 * @returns {boolean} - True if form is valid
 */
function validateForm(formId) {
    const form = document.getElementById(formId);
    if (!form) return false;

    let isValid = true;
    const errors = [];

    form.querySelectorAll("input, textarea, select").forEach(field => {
        // Check if field is required and empty
        if (field.hasAttribute("required") && !field.value.trim()) {
            errors.push(`${field.name || field.id}: ${t("fieldIsRequired")}`);
            field.classList.add("field-error");
            isValid = false;
        } else {
            field.classList.remove("field-error");
        }

        // Check number ranges
        if (field.type === "number") {
            const min = field.getAttribute("min");
            const max = field.getAttribute("max");
            const val = parseInt(field.value);

            if (min && val < parseInt(min)) {
                errors.push(`${field.name || field.id} must be at least ${min}`);
                field.classList.add("field-error");
                isValid = false;
            }

            if (max && val > parseInt(max)) {
                errors.push(`${field.name || field.id} must not exceed ${max}`);
                field.classList.add("field-error");
                isValid = false;
            }
        }
    });

    if (!isValid) {
        errors.forEach(error => showToast(error, "error"));
    }

    return isValid;
}

// ==================== BREADCRUMB NAVIGATION ====================
/**
 * Update breadcrumb navigation
 * @param {Array} items - Array of {label, tab}
 */
function updateBreadcrumb(items) {
    const breadcrumb = document.getElementById("breadcrumb");
    if (!breadcrumb) return;

    breadcrumb.innerHTML = "";

    items.forEach((item, index) => {
        const isLast = index === items.length - 1;

        if (isLast || !item.tab) {
            const span = document.createElement("span");
            span.className = "breadcrumb-item active";
            span.textContent = safeText(item.label);
            breadcrumb.appendChild(span);
            return;
        }

        const link = document.createElement("a");
        link.href = "#";
        link.className = "breadcrumb-item";
        link.dataset.tab = item.tab;
        link.textContent = safeText(item.label);
        breadcrumb.appendChild(link);

        const separator = document.createElement("span");
        separator.className = "breadcrumb-separator";
        separator.textContent = "/";
        breadcrumb.appendChild(separator);
    });
}

// ==================== THEME TOGGLE ====================
/**
 * Toggle between dark and light theme
 */
function toggleTheme() {
    const currentTheme = localStorage.getItem("theme") || "dark";
    const nextTheme = currentTheme === "dark" ? "light" : "dark";
    applyTheme(nextTheme);
}

/**
 * Initialize theme from localStorage
 */
function initializeTheme() {
    const theme = localStorage.getItem("theme") || "dark";
    applyTheme(theme);
}

function updateThemeToggleUi(theme) {
    const icon = document.querySelector("#theme-toggle .theme-icon");
    const label = document.querySelector("#theme-toggle .theme-label");

    if (icon) {
        icon.textContent = theme === "dark" ? "🌙" : "☀️";
    }

    if (label) {
        label.textContent = theme === "dark" ? t("darkMode") : t("lightMode");
    }
}

function applyTheme(theme) {
    const root = document.documentElement;
    const isLight = theme === "light";

    if (isLight) {
        root.style.setProperty("--bg-primary", "#FFFFFF");
        root.style.setProperty("--bg-secondary", "#F5F5F5");
        root.style.setProperty("--bg-tertiary", "#EEEEEE");
        root.style.setProperty("--text-primary", "#000000");
        root.style.setProperty("--text-secondary", "#555555");
        root.style.setProperty("--text-tertiary", "#999999");
        root.style.setProperty("--border-light", "#DDDDDD");
        root.style.setProperty("--border-dark", "#CCCCCC");
    } else {
        root.style.setProperty("--bg-primary", "#0F0F0F");
        root.style.setProperty("--bg-secondary", "#1A1A1A");
        root.style.setProperty("--bg-tertiary", "#242424");
        root.style.setProperty("--text-primary", "#FFFFFF");
        root.style.setProperty("--text-secondary", "#B0B0B0");
        root.style.setProperty("--text-tertiary", "#808080");
        root.style.setProperty("--border-light", "#333");
        root.style.setProperty("--border-dark", "#222");
    }

    updateThemeToggleUi(theme);
    localStorage.setItem("theme", theme);
}

// ==================== REAL-TIME STATUS UPDATES ====================
/**
 * Update status element with pulse animation
 * @param {string} elementId - Element ID
 * @param {string} newValue - New value to display
 */
function updateStatusWithPulse(elementId, newValue) {
    const nextValue = safeText(newValue);
    const el = document.getElementById(elementId);
    if (!el || el.textContent === nextValue) return;

    el.classList.add("status-update");
    el.textContent = nextValue;

    setTimeout(() => {
            el.classList.remove("status-update");
        },
        600);
}

function formatGigabytes(value) {
    const gigabytes = Number(value);
    if (!Number.isFinite(gigabytes)) {
        return "-";
    }

    return gigabytes.toLocaleString(undefined, {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2
    });
}

function formatVersionWithBuild(version, build) {
    const normalizedVersion = safeText(version).trim();
    if (!normalizedVersion || normalizedVersion === "-") {
        return "-";
    }

    const numericBuild = Number(build);
    return Number.isFinite(numericBuild) && numericBuild > 0
        ? `${normalizedVersion} (${numericBuild})`
        : normalizedVersion;
}

function applyTotalGbGradient(valueGb) {
    const totalGb = Number(valueGb);
    const totalGbElement = document.getElementById("total-gb");
    const statBox = totalGbElement?.closest(".stat-box");
    if (!(statBox instanceof HTMLElement)) return;

    statBox.classList.remove("gb-level-low", "gb-level-medium", "gb-level-high", "gb-level-ultra");

    if (!Number.isFinite(totalGb)) return;

    if (totalGb >= 100) {
        statBox.classList.add("gb-level-ultra");
    } else if (totalGb >= 10) {
        statBox.classList.add("gb-level-high");
    } else if (totalGb >= 1) {
        statBox.classList.add("gb-level-medium");
    } else {
        statBox.classList.add("gb-level-low");
    }
}

function renderCurrentRosDownload(activity) {
    const fileEl = document.getElementById("current-ros-file");
    const metaEl = document.getElementById("current-ros-meta");
    if (!fileEl || !metaEl) return;

    const isChecking = Boolean(activity?.isChecking);
    const activeDownloads = Number(activity?.activeDownloads || 0);
    const currentFile = safeText(activity?.currentFile || "");
    const currentVersion = safeText(activity?.currentVersion || "");
    const currentStartedAt = safeText(activity?.currentStartedAt || "");

    const lastFile = safeText(activity?.lastFile || "");
    const lastVersion = safeText(activity?.lastVersion || "");
    const lastCompletedAt = safeText(activity?.lastCompletedAt || "");

    if (activeDownloads > 0 && currentFile && currentFile !== "-") {
        fileEl.textContent = currentFile;
        const startedText = currentStartedAt ? `, ${t("startedAt")} ${formatDateTime(currentStartedAt)}` : "";
        metaEl.textContent = `${t("downloadingRouterOs")} ${currentVersion || "-"} (${activeDownloads} ${t("activeDownloads")}${startedText})`;
        return;
    }

    if (isChecking) {
        fileEl.textContent = t("checkingMikroTikServers");
        metaEl.textContent = t("resolvingVersions");
        return;
    }

    if (lastFile && lastFile !== "-") {
        fileEl.textContent = lastFile;
        const completedText = lastCompletedAt ? ` ${t("completedAt")} ${formatDateTime(lastCompletedAt)}` : "";
        metaEl.textContent = `${t("lastDownloaded")}: RouterOS ${lastVersion || "-"}${completedText}`;
        return;
    }

    fileEl.textContent = "-";
    metaEl.textContent = t("noActiveMikroTikDownload");
}

function formatTlsProbeHeadline(probe) {
    if (!probe) return "-";

    if (probe.isSuccess) {
        const httpPart = Number.isFinite(Number(probe.httpStatus))
            ? `HTTP ${probe.httpStatus}`
            : "Connected";
        const latencyPart = Number.isFinite(Number(probe.latencyMs))
            ? `, ${probe.latencyMs} ms`
            : "";
        return `✓ ${httpPart}${latencyPart}`;
    }

    const category = safeText(probe.failureCategory || "error").replaceAll("_", " ");
    return `✗ ${category}`;
}

function formatTlsProbeDetails(probe) {
    if (!probe) return "-";

    if (probe.isSuccess) {
        return t("tlsDiagnosticsHealthy");
    }

    const firstError = Array.isArray(probe.exceptionChain) && probe.exceptionChain.length > 0
        ? safeText(probe.exceptionChain[0].message || "")
        : "";

    const baseMessage = firstError || safeText(probe.message || "");
    const recommendation = safeText(probe.recommendation || "");
    const details = recommendation ? `${baseMessage} | ${recommendation}` : baseMessage;
    return details || t("tlsDiagnosticsNoDetails");
}

function renderTlsDiagnostics(probe) {
    const headlineEl = document.getElementById("tls-probe");
    const detailsEl = document.getElementById("tls-details");
    if (!headlineEl || !detailsEl) return;

    updateStatusWithPulse("tls-probe", formatTlsProbeHeadline(probe));
    updateStatusWithPulse("tls-details", formatTlsProbeDetails(probe));
}

async function loadTlsDiagnostics(force = false) {
    const now = Date.now();
    if (!force && now - lastTlsDiagnosticsRefresh < TLS_DIAGNOSTICS_REFRESH_MS && cachedTlsProbe) {
        renderTlsDiagnostics(cachedTlsProbe);
        return;
    }

    const target = encodeURIComponent("https://upgrade.mikrotik.com/routeros/NEWEST7.stable");
    try {
        const response = await fetch(`${API_BASE}/health/tls?target=${target}&timeoutSeconds=8`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const payload = await response.json();
        cachedTlsProbe = payload?.probe || null;
        lastTlsDiagnosticsRefresh = Date.now();
        renderTlsDiagnostics(cachedTlsProbe);
    } catch (err) {
        console.error("Error loading TLS diagnostics:", err);
        cachedTlsProbe = {
            isSuccess: false,
            failureCategory: "diagnostics_unavailable",
            message: err?.message || "TLS diagnostics request failed",
            recommendation: t("tlsDiagnosticsUnavailable")
        };
        renderTlsDiagnostics(cachedTlsProbe);
    }
}

// ==================== SEARCH/FILTER ====================
/**
 * Filter versions table by search text
 * @param {string} searchText - Text to search
 */
function filterVersionsTable(searchText) {
    const rows = document.querySelectorAll(".versions-table tbody tr");
    let visibleCount = 0;

    rows.forEach(row => {
        const text = row.textContent.toLowerCase();
        const isVisible = text.includes(searchText.toLowerCase());
        row.style.display = isVisible ? "" : "none";
        if (isVisible) visibleCount++;
    });

    refreshSelectedVersionsCount();

    // Show "no results" message if needed
    if (visibleCount === 0 && searchText) {
        showToast(t("noVersionsFound"), "info");
    }
}

// ==================== MOBILE SIDEBAR TOGGLE ====================
/**
 * Toggle sidebar on mobile
 */
function toggleSidebar() {
    const sidebar = document.getElementById("sidebar");
    const toggle = document.getElementById("sidebar-toggle");
    if (!sidebar || !toggle) return;

    sidebar.classList.toggle("open");
    toggle.classList.toggle("active");

    // Save state to localStorage
    const isOpen = sidebar.classList.contains("open");
    toggle.setAttribute("aria-expanded", String(isOpen));
    localStorage.setItem("sidebarOpen", isOpen);
}

/**
 * Close sidebar (for when link is clicked on mobile)
 */
function closeSidebarOnMobile() {
    if (window.innerWidth <= 768) {
        const sidebar = document.getElementById("sidebar");
        const toggle = document.getElementById("sidebar-toggle");
        if (!sidebar || !toggle) return;
        sidebar.classList.remove("open");
        toggle.classList.remove("active");
        toggle.setAttribute("aria-expanded", "false");
    }
}

// ==================== KEYBOARD SHORTCUTS ====================
/**
 * Setup keyboard shortcuts
 */
function setupTabListKeyboardNavigation(tabList, getTabs, activateTab) {
    if (!tabList) return;

    tabList.addEventListener("keydown", (e) => {
        const tabs = getTabs();
        const currentTab = e.target.closest('[role="tab"]');
        if (!currentTab || tabs.length === 0) return;

        const currentIndex = tabs.indexOf(currentTab);
        if (currentIndex === -1) return;

        let nextIndex = currentIndex;
        switch (e.key) {
            case "ArrowRight":
            case "ArrowDown":
                nextIndex = (currentIndex + 1) % tabs.length;
                break;
            case "ArrowLeft":
            case "ArrowUp":
                nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
                break;
            case "Home":
                nextIndex = 0;
                break;
            case "End":
                nextIndex = tabs.length - 1;
                break;
            case "Enter":
            case " ":
                e.preventDefault();
                activateTab(currentTab);
                return;
            default:
                return;
        }

        e.preventDefault();
        tabs[nextIndex].focus();
    });
}

function setupKeyboardShortcuts() {
    document.addEventListener("keydown",
        (e) => {
            // Alt + 1-6 for tab switching
            if (e.altKey && e.key >= "1" && e.key <= "6") {
                e.preventDefault();
                activateMainTab(MAIN_TABS[e.key - 1]);
            }

            // Alt + S for sidebar toggle
            if (e.altKey && e.key === "s") {
                e.preventDefault();
                toggleSidebar();
            }

            // Ctrl/Cmd + K for search
            if ((e.ctrlKey || e.metaKey) && e.key === "k") {
                e.preventDefault();
                const activeTab = document.querySelector(".tab-pane.active")?.id;
                const searchInput = activeTab === "logs"
                    ? document.getElementById("log-search")
                    : document.getElementById("versions-search");
                if (searchInput) {
                    searchInput.focus();
                }
            }

            // Escape to close sidebar on mobile
            if (e.key === "Escape" && window.innerWidth <= 768) {
                const sidebar = document.getElementById("sidebar");
                if (sidebar.classList.contains("open")) {
                    toggleSidebar();
                }
            }
        });
}

function handleVisibilityChange() {
    if (document.hidden) {
        stopPeriodicUpdates();
        if (autoRefreshInterval) {
            clearInterval(autoRefreshInterval);
            autoRefreshInterval = null;
        }
        if (scheduleUpdateInterval) {
            clearInterval(scheduleUpdateInterval);
            scheduleUpdateInterval = null;
        }
        return;
    }

    startPeriodicUpdates();

    const activePane = document.querySelector(".tab-pane.active");
    if (activePane?.id === "logs") {
        loadLogs();
        if (document.getElementById("auto-refresh")?.checked) {
            toggleAutoRefresh();
        }
    }

    if (activePane?.id === "dashboard") {
        loadDashboard();
        loadTodayClientUpdates(true);
    }

    if (activePane?.id === "schedule") {
        loadSchedule();
        if (scheduleUpdateInterval) clearInterval(scheduleUpdateInterval);
        scheduleUpdateInterval = setInterval(loadSchedule, 60000);
    }

    if (activePane?.id === "config") {
        loadPointerRouting(true);
    }
}

function bindUiEvents() {
    document
        .querySelectorAll(".nav-link[data-tab]")
        .forEach((link) => link.addEventListener("click", (e) => switchTab(e, link.dataset.tab)));

    const sidebarToggle = document.getElementById("sidebar-toggle");
    if (sidebarToggle) {
        sidebarToggle.addEventListener("click", toggleSidebar);
    }

    const languageSelect = document.getElementById("language-select");
    if (languageSelect) {
        languageSelect.addEventListener("change", (e) => changeLanguage(e.target.value));
    }

    const themeToggle = document.getElementById("theme-toggle");
    if (themeToggle) {
        themeToggle.addEventListener("click", toggleTheme);
    }

    const checkUpdatesBtn = document.getElementById("check-updates-btn");
    if (checkUpdatesBtn) {
        checkUpdatesBtn.addEventListener("click", checkUpdates);
    }

    const openShortcutsBtn = document.getElementById("open-shortcuts-btn");
    if (openShortcutsBtn) {
        openShortcutsBtn.addEventListener("click", showKeyboardShortcuts);
    }

    const versionsSearch = document.getElementById("versions-search");
    if (versionsSearch) {
        versionsSearch.addEventListener("input", (e) => filterVersionsTable(e.target.value));
    }

    const selectVisibleVersionsBtn = document.getElementById("select-visible-versions-btn");
    if (selectVisibleVersionsBtn) {
        selectVisibleVersionsBtn.addEventListener("click", selectVisibleVersions);
    }

    const deleteSelectedVersionsBtn = document.getElementById("delete-selected-versions-btn");
    if (deleteSelectedVersionsBtn) {
        deleteSelectedVersionsBtn.addEventListener("click", deleteSelectedVersions);
    }

    const logLevel = document.getElementById("log-level");
    if (logLevel) {
        logLevel.addEventListener("change", loadLogs);
    }

    const logSearch = document.getElementById("log-search");
    if (logSearch) {
        logSearch.addEventListener("input", debounceLoadLogs);
    }

    const logLimit = document.getElementById("log-limit");
    if (logLimit) {
        logLimit.addEventListener("change", loadLogs);
    }

    const refreshLogsBtn = document.getElementById("refresh-logs-btn");
    if (refreshLogsBtn) {
        refreshLogsBtn.addEventListener("click", loadLogs);
    }

    const downloadLogsBtn = document.getElementById("download-logs-btn");
    if (downloadLogsBtn) {
        downloadLogsBtn.addEventListener("click", downloadLogs);
    }

    const clearLogFiltersBtn = document.getElementById("clear-log-filters-btn");
    if (clearLogFiltersBtn) {
        clearLogFiltersBtn.addEventListener("click", clearLogFilters);
    }

    const autoRefreshCheckbox = document.getElementById("auto-refresh");
    if (autoRefreshCheckbox) {
        autoRefreshCheckbox.addEventListener("change", toggleAutoRefresh);
    }

    const saveConsoleLogsBtn = document.getElementById("save-console-logs-btn");
    if (saveConsoleLogsBtn) {
        saveConsoleLogsBtn.addEventListener("click", saveConsoleLogSettings);
    }

    const pauseScheduleBtn = document.getElementById("pause-schedule-btn");
    if (pauseScheduleBtn) {
        pauseScheduleBtn.addEventListener("click", pauseSchedule);
    }

    const resumeScheduleBtn = document.getElementById("resume-schedule-btn");
    if (resumeScheduleBtn) {
        resumeScheduleBtn.addEventListener("click", resumeSchedule);
    }

    document
        .querySelectorAll("[data-version-tab]")
        .forEach((button) =>
            button.addEventListener("click", (e) => switchVersionTab(e, button.dataset.versionTab)));

    document
        .querySelectorAll("[data-changelog-tab]")
        .forEach((button) =>
            button.addEventListener("click", (e) => switchChangelogTab(e, button.dataset.changelogTab)));

    const refreshGlobalChangelogBtn = document.getElementById("refresh-global-changelog-btn");
    if (refreshGlobalChangelogBtn) {
        refreshGlobalChangelogBtn.addEventListener("click", loadGlobalChangelog);
    }

    const loadVersionChangelogBtn = document.getElementById("load-version-changelog-btn");
    if (loadVersionChangelogBtn) {
        loadVersionChangelogBtn.addEventListener("click", loadVersionChangelog);
    }

    const refreshVersionHistoryBtn = document.getElementById("refresh-version-history-btn");
    if (refreshVersionHistoryBtn) {
        refreshVersionHistoryBtn.addEventListener("click", loadVersionHistory);
    }

    const versionSelect = document.getElementById("version-select");
    if (versionSelect) {
        versionSelect.addEventListener("change", loadVersionChangelog);
    }

    document.querySelectorAll(".copy-btn[data-copy-target]").forEach((button) => {
        button.addEventListener("click", () => copyCode(button.dataset.copyTarget));
    });

    const saveArchesBtn = document.getElementById("save-arches-btn");
    if (saveArchesBtn) {
        saveArchesBtn.addEventListener("click", saveAllowedArches);
    }

    const saveV7PackagesBtn = document.getElementById("save-v7-packages-btn");
    if (saveV7PackagesBtn) {
        saveV7PackagesBtn.addEventListener("click", saveV7Packages);
    }

    const saveDeletePrefixesBtn = document.getElementById("save-delete-prefixes-btn");
    if (saveDeletePrefixesBtn) {
        saveDeletePrefixesBtn.addEventListener("click", saveDeletePrefixes);
    }

    const reloadDeletePrefixesBtn = document.getElementById("reload-delete-prefixes-btn");
    if (reloadDeletePrefixesBtn) {
        reloadDeletePrefixesBtn.addEventListener("click", loadDeletePrefixes);
    }

    const reloadPointerRoutingBtn = document.getElementById("reload-pointer-routing-btn");
    if (reloadPointerRoutingBtn) {
        reloadPointerRoutingBtn.addEventListener("click", () => loadPointerRouting(true, true));
    }

    const pointerRoutingBody = document.getElementById("pointer-routing-list");
    if (pointerRoutingBody) {
        pointerRoutingBody.addEventListener("click", (e) => {
            const target = e.target instanceof Element ? e.target : null;
            const applyButton = target?.closest(".pointer-route-apply-btn");
            if (!applyButton) return;

            const row = applyButton.closest("tr");
            const select = row?.querySelector(".pointer-route-select");
            const pointer = safeText(select?.dataset?.pointer || applyButton.dataset.pointer || "");
            const branch = safeText(select?.value || "");
            savePointerRoute(pointer, branch, row);
        });
    }

    const exportSettingsBtn = document.getElementById("export-settings-btn");
    if (exportSettingsBtn) {
        exportSettingsBtn.addEventListener("click", exportSettings);
    }

    const importSettingsBtn = document.getElementById("import-settings-btn");
    if (importSettingsBtn) {
        importSettingsBtn.addEventListener("click", importSettingsClick);
    }

    const importFileInput = document.getElementById("import-file");
    if (importFileInput) {
        importFileInput.addEventListener("change", importSettings);
    }

    const modal = document.getElementById("shortcuts-modal");
    if (modal) {
        modal.addEventListener("click", (e) => {
            if (e.target === modal) {
                closeShortcutsModal();
            }
        });
    }

    const closeShortcutsBtn = document.getElementById("close-shortcuts-modal-btn");
    if (closeShortcutsBtn) {
        closeShortcutsBtn.addEventListener("click", closeShortcutsModal);
    }

    const scheduleForm = document.getElementById("schedule-form");
    if (scheduleForm) {
        scheduleForm.addEventListener("submit", saveSchedule);
    }

    const breadcrumb = document.getElementById("breadcrumb");
    if (breadcrumb) {
        breadcrumb.addEventListener("click", (e) => {
            const link = e.target.closest(".breadcrumb-item[data-tab]");
            if (!link) return;
            switchTab(e, link.dataset.tab);
        });
    }

    const sidebarNav = document.querySelector(".sidebar-nav[role='tablist']");
    setupTabListKeyboardNavigation(
        sidebarNav,
        () => Array.from(sidebarNav?.querySelectorAll('[role="tab"]') || []),
        (tab) => activateMainTab(tab.dataset.tab, tab)
    );

    const versionTabList = document.querySelector("#versions .tabs-bar[role='tablist']");
    setupTabListKeyboardNavigation(
        versionTabList,
        () => Array.from(versionTabList?.querySelectorAll('[role="tab"]') || []),
        (tab) => activateVersionTab(tab.dataset.versionTab, tab)
    );

    const changelogTabList = document.querySelector("#changelog .tabs-bar[role='tablist']");
    setupTabListKeyboardNavigation(
        changelogTabList,
        () => Array.from(changelogTabList?.querySelectorAll('[role="tab"]') || []),
        (tab) => activateChangelogTab(tab.dataset.changelogTab, tab)
    );

    document.addEventListener("change", (e) => {
        if (e.target?.classList?.contains("version-select-checkbox")) {
            refreshSelectedVersionsCount();
        }
    });
}

document.addEventListener("DOMContentLoaded", () => {
    bindUiEvents();

    initializeTheme();
    setupKeyboardShortcuts();
    document.addEventListener("visibilitychange", handleVisibilityChange);

    if (window.innerWidth <= 768) {
        const sidebarOpen = localStorage.getItem("sidebarOpen") === "true";
        if (sidebarOpen) {
            const sidebar = document.getElementById("sidebar");
            const toggle = document.getElementById("sidebar-toggle");
            sidebar?.classList.add("open");
            toggle?.classList.add("active");
            toggle?.setAttribute("aria-expanded", "true");
        }
    }

    initLocalization()
        .then(() => {
            applyTranslations();
        })
        .finally(() => {
            loadDashboard();
            loadVersions();
            loadGlobalChangelog();
            loadConsoleLogSettings();
            startPeriodicUpdates();
            activateMainTab("dashboard");
        });
});

/**
 * Logs Management
 */

// Debounce для поиска
let searchTimeout = null;

function debounceLoadLogs() {
    if (searchTimeout) clearTimeout(searchTimeout);
    searchTimeout = setTimeout(loadLogs, 500);
}

function clearLogFilters() {
    document.getElementById("log-level").value = "";
    document.getElementById("log-search").value = "";
    document.getElementById("log-limit").value = "100";
    loadLogs();
}

function toggleAutoRefresh() {
    const autoRefresh = document.getElementById("auto-refresh").checked;

    if (autoRefreshInterval) {
        clearInterval(autoRefreshInterval);
        autoRefreshInterval = null;
    }

    if (autoRefresh) {
        autoRefreshInterval = setInterval(loadLogs, 10000); // 10 seconds
        console.log("Auto-refresh enabled");
    } else {
        console.log("Auto-refresh disabled");
    }
}

async function loadConsoleLogSettings() {
    const enabledCheckbox = document.getElementById("console-logs-enabled");
    const levelSelect = document.getElementById("console-log-level");
    const statusEl = document.getElementById("console-logs-status");

    if (!enabledCheckbox || !levelSelect) return;

    try {
        const response = await fetch(`${API_BASE}/settings/console-logs`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const data = await response.json();
        const levels = Array.isArray(data.levels) && data.levels.length
            ? data.levels
            : ["Debug", "Information", "Warning", "Error"];
        const currentLevel = safeText(data.level || "Information");

        enabledCheckbox.checked = Boolean(data.enabled);
        levelSelect.innerHTML = "";

        levels.forEach((level) => {
            const option = document.createElement("option");
            option.value = level;
            option.textContent = t(level.toLowerCase()) || level;
            levelSelect.appendChild(option);
        });

        if (!levels.includes(currentLevel)) {
            levelSelect.value = levels[0];
        } else {
            levelSelect.value = currentLevel;
        }

        if (statusEl) statusEl.textContent = "";
    } catch (error) {
        console.error("Error loading console log settings:", error);
        if (statusEl) {
            statusEl.textContent = t("errorLoadingConsoleLogSettings");
            statusEl.style.color = "var(--danger)";
        }
    }
}

async function saveConsoleLogSettings() {
    const enabledCheckbox = document.getElementById("console-logs-enabled");
    const levelSelect = document.getElementById("console-log-level");
    const statusEl = document.getElementById("console-logs-status");

    if (!enabledCheckbox || !levelSelect) return;

    try {
        const response = await fetch(`${API_BASE}/settings/console-logs`, {
            method: "POST",
            headers: {"Content-Type": "application/json"},
            body: JSON.stringify({
                enabled: enabledCheckbox.checked,
                level: levelSelect.value
            })
        });

        if (!response.ok) {
            const err = await response.json().catch(() => ({}));
            throw new Error(err.message || `HTTP ${response.status}`);
        }

        if (statusEl) {
            statusEl.textContent = t("consoleLogSettingsSaved");
            statusEl.style.color = "var(--success)";
        }

        showToast(t("consoleLogSettingsSaved"), "success");
        await loadConsoleLogSettings();
    } catch (error) {
        console.error("Error saving console log settings:", error);
        if (statusEl) {
            statusEl.textContent = t("errorSavingConsoleLogSettings");
            statusEl.style.color = "var(--danger)";
        }
        showToast(`${t("errorSavingConsoleLogSettings")}: ${error.message}`, "error");
    }
}

async function loadLogs() {
    const level = document.getElementById("log-level").value;
    const search = document.getElementById("log-search").value;
    const limit = document.getElementById("log-limit").value;

    const params = new window.URLSearchParams();
    if (level) params.append("level", level);
    if (search) params.append("search", search);
    if (limit) params.append("take", limit);

    showLoading("logs-content", t("loadingLogs"));

    try {
        const response = await fetch(`${API_BASE}/logs?${params}`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const data = await response.json();
        displayLogs(data.logs);
        void loadLogStats();
    } catch (error) {
        console.error("Error loading logs:", error);
        const container = document.getElementById("logs-content");
        if (container) {
            container.innerHTML = "";
            const entry = document.createElement("div");
            entry.className = "log-entry error";
            entry.textContent = `${t("errorLoadingLogs")}: ${safeText(error.message)}`;
            container.appendChild(entry);
        }
        showToast(`${t("errorLoadingLogs")}: ${safeText(error.message)}`, "error");
    }
}

async function loadLogStats() {
    try {
        const response = await fetchWithTimeout(`${API_BASE}/logs/stats`, {}, LOG_STATS_TIMEOUT_MS);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const stats = await response.json();
        displayLogStats(stats);
    } catch (error) {
        if (error?.name !== "TimeoutError") {
            console.warn("Log stats are unavailable:", error);
        }
        displayLogStatsUnavailable();
    }
}

function displayLogStatsUnavailable() {
    const statsDiv = document.getElementById("logs-stats");
    if (!statsDiv) return;

    statsDiv.innerHTML = `
        <div class="empty-state">
            <div class="empty-state-icon">📊</div>
            <p>${safeHtml(t("logStatsUnavailable"))}</p>
        </div>
    `;
}

function displayLogStats(stats) {
    const statsDiv = document.getElementById("logs-stats");
    if (!statsDiv) return;

    statsDiv.innerHTML = `
        <div class="stats-grid">
            <div class="stat-item">
                <span class="stat-number">${safeHtml(stats.totalEntries)}</span>
                <span class="stat-label">${safeHtml(t("totalEntries"))}</span>
            </div>
            <div class="stat-item info">
                <span class="stat-number">${safeHtml(stats.infoCount)}</span>
                <span class="stat-label">${safeHtml(t("information"))}</span>
            </div>
            <div class="stat-item warning">
                <span class="stat-number">${safeHtml(stats.warningCount)}</span>
                <span class="stat-label">${safeHtml(t("warnings"))}</span>
            </div>
            <div class="stat-item error">
                <span class="stat-number">${safeHtml(stats.errorCount)}</span>
                <span class="stat-label">${safeHtml(t("errors"))}</span>
            </div>
            <div class="stat-item">
                <span class="stat-label">${safeHtml(t("timeRange"))}</span>
                <span class="stat-value">${safeHtml(formatDate(
            stats.oldestEntry
        ))} - ${safeHtml(formatDate(stats.newestEntry))}</span>
            </div>
        </div>
    `;
}

function displayLogs(logs) {
    const container = document.getElementById("logs-content");
    if (!container) return;

    container.innerHTML = "";

    if (!logs || logs.length === 0) {
        const emptyState = document.createElement("div");
        emptyState.className = "log-entry";
        emptyState.textContent = t("noLogsFound");
        container.appendChild(emptyState);
        return;
    }

    const fragment = document.createDocumentFragment();

    logs.forEach((log) => {
        const levelText = safeText(log.level || "Information");
        const levelClass = levelText.toLowerCase().replace(/[^a-z0-9_-]/g, "") || "information";

        const entry = document.createElement("div");
        entry.className = `log-entry ${levelClass}`;

        const timestamp = document.createElement("span");
        timestamp.className = "log-col timestamp";
        timestamp.textContent = formatDateTime(log.timestamp);
        entry.appendChild(timestamp);

        const levelCol = document.createElement("span");
        levelCol.className = "log-col level";

        const levelBadge = document.createElement("span");
        levelBadge.className = `level-badge ${levelClass}`;
        levelBadge.textContent = levelText;
        levelCol.appendChild(levelBadge);
        entry.appendChild(levelCol);

        const source = document.createElement("span");
        source.className = "log-col source";
        source.title = safeText(log.source);
        source.textContent = truncateText(safeText(log.source), 30);
        entry.appendChild(source);

        const message = document.createElement("span");
        message.className = "log-col message";
        message.title = safeText(log.message);
        message.textContent = truncateText(safeText(log.message), 100);

        if (log.exception) {
            const indicator = document.createElement("span");
            indicator.className = "exception-indicator";
            indicator.title = t("containsException");
            indicator.textContent = "⚠️";
            message.appendChild(indicator);
        }

        entry.appendChild(message);
        fragment.appendChild(entry);
    });

    container.appendChild(fragment);
}

async function downloadLogs() {
    try {
        showToast(t("startingDownload"), "info");
        const response = await fetch(`${API_BASE}/logs/download`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const blob = await response.blob();
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = `logs-${new Date()
            .toISOString()
            .slice(0, 19)
            .replace(/:/g, "-")}.zip`;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(url);
        document.body.removeChild(a);

        showToast(t("logsDownloadedSuccessfully"), "success");
    } catch (error) {
        console.error("Error downloading logs:", error);
        showToast(`${t("errorDownloadingLogs")}: ${error.message}`, "error");
    }
}

/**
 * Schedule Management
 */

async function loadSchedule() {
    try {
        // Проверяем, существует ли элемент формы
        const scheduleForm = document.getElementById("schedule-form");
        if (!scheduleForm) {
            console.warn(t("scheduleFormNotFound"));
            return;
        }

        const [configResponse, statusResponse] = await Promise.all([
            fetch(`${API_BASE}/schedule`),
            fetch(`${API_BASE}/schedule/status`)
        ]);

        if (!configResponse.ok || !statusResponse.ok) {
            throw new Error(t("failedToLoadScheduleData"));
        }

        const config = await configResponse.json();
        const status = await statusResponse.json();

        displaySchedule(config, status);
    } catch (error) {
        console.error("Error loading schedule:", error);
        showToast(`${t("errorLoadingSchedule")}: ${error.message}`, "error");
    }
}

function displaySchedule(config, status) {
    // Update status card
    document.getElementById("schedule-status-badge").textContent = translateScheduleStatus(status.status);
    document.getElementById(
        "schedule-status-badge"
    ).className = `status-badge ${status.status.toLowerCase()}`;

    document.getElementById("next-check-time").textContent =
        status.nextScheduledCheck
        ? formatDateTime(status.nextScheduledCheck)
        : t("never");

    document.getElementById("time-until-check").textContent =
        status.timeUntilNextCheck ? formatTimeSpan(status.timeUntilNextCheck) : "-";

    document.getElementById("paused-until").textContent = status.config
        .pausedUntil
        ? formatDateTime(status.config.pausedUntil)
        : t("notPaused");

    // Update form
    document.getElementById("schedule-enabled").checked = config.enabled;
    document.getElementById("check-time").value = config.checkTime.substring(
        0,
        5
    ); // HH:mm format
    document.getElementById("check-interval").value = config.intervalMinutes;
    document.getElementById("notify-completion").checked =
        config.notifyOnCompletion;
    document.getElementById("notify-errors").checked = config.notifyOnError;

    // Update days checkboxes
    const dayCheckboxes = document.querySelectorAll('input[name="days"]');
    dayCheckboxes.forEach((checkbox) => {
        checkbox.checked = config.daysOfWeek.includes(checkbox.value);
    });
}

async function saveSchedule(event) {
    event.preventDefault();

    // Validate form
    if (!validateForm("schedule-form")) {
        return;
    }

    const formData = new FormData(event.target);
    const selectedDays = Array.from(
        document.querySelectorAll('input[name="days"]:checked')
    ).map((cb) => cb.value);

    // Validate at least one day selected
    if (selectedDays.length === 0) {
        showToast(t("selectAtLeastOneDay"), "error");
        return;
    }

    const config = {
        enabled: formData.get("enabled") === "on",
        checkTime: `${formData.get("checkTime")}:00`,
        intervalMinutes: parseInt(formData.get("intervalMinutes")),
        daysOfWeek: selectedDays,
        notifyOnCompletion: formData.get("notifyOnCompletion") === "on",
        notifyOnError: formData.get("notifyOnError") === "on"
    };

    try {
        const response = await fetch(`${API_BASE}/schedule`,
            {
                method: "POST",
                headers: {
                    "Content-Type": "application/json"
                },
                body: JSON.stringify(config)
            });

        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        showToast(t("scheduleSavedSuccessfully"), "success");
        await loadSchedule(); // Reload to get updated status
    } catch (error) {
        console.error("Error saving schedule:", error);
        showToast(`${t("errorSavingSchedule")}: ${error.message}`, "error");
    }
}

async function pauseSchedule() {
    const duration = document.getElementById("pause-duration").value;

    if (!confirm(`${t("pauseUpdatesFor")} ${duration} ${t("hoursShortQuestion")}`)) return;

    try {
        const response = await fetch(
            `${API_BASE}/schedule/pause?hours=${duration}`,
            {
                method: "POST"
            }
        );

        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        showToast(`${t("updatesPausedFor")} ${duration} ${t("hoursShort")}`, "warning");
        await loadSchedule(); // Reload to get updated status
    } catch (error) {
        console.error("Error pausing schedule:", error);
        showToast(`${t("errorPausingSchedule")}: ${error.message}`, "error");
    }
}

async function resumeSchedule() {
    try {
        const response = await fetch(`${API_BASE}/schedule/resume`,
            {
                method: "POST"
            });

        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        showToast(t("updatesResumed"), "success");
        await loadSchedule(); // Reload to get updated status
    } catch (error) {
        console.error("Error resuming schedule:", error);
        showToast(`${t("errorResumingSchedule")}: ${error.message}`, "error");
    }
}

/**
 * Utility Functions
 */

function formatDateTime(dateString) {
    const date = new Date(dateString);
    return date.toLocaleString();
}

function formatDate(dateString) {
    const date = new Date(dateString);
    return date.toLocaleDateString();
}

function parseTimeSpanToMilliseconds(value) {
    if (typeof value === "number" && Number.isFinite(value)) {
        return value;
    }

    const text = safeText(value).trim();
    if (!text) {
        return NaN;
    }

    const numeric = Number(text);
    if (Number.isFinite(numeric)) {
        return numeric;
    }

    const parts = text.split(":");
    if (parts.length < 2) {
        return NaN;
    }

    let hoursPart = safeText(parts[0]).trim();
    const minutesPart = safeText(parts[1]).trim();
    const secondsPart = safeText(parts[2] ?? "0").trim().replace(",", ".");

    let days = 0;
    if (hoursPart.includes(".")) {
        const chunks = hoursPart.split(".", 2);
        days = Number(chunks[0]);
        hoursPart = safeText(chunks[1]).trim();
    }

    const hours = Number(hoursPart);
    const minutes = Number(minutesPart);
    const seconds = Number(secondsPart);
    if (!Number.isFinite(days) || !Number.isFinite(hours) || !Number.isFinite(minutes) || !Number.isFinite(seconds)) {
        return NaN;
    }

    return (((days * 24 + hours) * 3600) + (minutes * 60) + seconds) * 1000;
}

function formatTimeSpan(value) {
    const milliseconds = parseTimeSpanToMilliseconds(value);
    if (!Number.isFinite(milliseconds) || milliseconds < 0) {
        return "-";
    }

    const totalSeconds = Math.floor(milliseconds / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const hoursLabel = translateOrFallback("hoursShortLabel", "h");
    const minutesLabel = translateOrFallback("minutesShort", "m");

    if (hours > 0) {
        return `${hours}${hoursLabel} ${minutes}${minutesLabel}`;
    }
    return `${minutes}${minutesLabel}`;
}

function formatUptime(uptime) {
    if (!uptime) return "-";

    const days = Number(uptime.days ?? 0);
    const hours = Number(uptime.hours ?? 0);
    const minutes = Number(uptime.minutes ?? 0);
    const safeDays = Number.isFinite(days) ? days : 0;
    const safeHours = Number.isFinite(hours) ? hours : 0;
    const safeMinutes = Number.isFinite(minutes) ? minutes : 0;

    return `${safeDays}${translateOrFallback("daysShort", "d")} ${safeHours}${translateOrFallback("hoursShortLabel", "h")} ${safeMinutes}${translateOrFallback("minutesShort", "m")}`;
}

function translateScheduleStatus(statusText) {
    const normalized = safeText(statusText).toLowerCase();
    if (normalized === "active") return t("scheduleStatusActive");
    if (normalized === "paused") return t("scheduleStatusPaused");
    if (normalized === "disabled") return t("scheduleStatusDisabled");
    return safeText(statusText);
}

function truncateText(text, maxLength) {
    if (!text) return "";
    return text.length > maxLength ? text.substring(0, maxLength) + "..." : text;
}

/**
 * Запускает периодические обновления данных
 */
function startPeriodicUpdates() {
    if (document.hidden) return;

    // /api/status каждые 5 секунд
    if (timers.status) clearInterval(timers.status);
    timers.status = setInterval(() => {
            loadDashboard();
        },
        INTERVALS.STATUS);

    // /api/versions каждые 60 секунд
    if (timers.versions) clearInterval(timers.versions);
    timers.versions = setInterval(() => {
            loadVersions();
        },
        INTERVALS.VERSIONS);
}

/**
 * Переключение между вкладками changelog
 */
function activateChangelogTab(tabName, sourceButton = null) {
    document.querySelectorAll(".changelog-tab").forEach((panel) => {
        const isActive = panel.id === tabName;
        panel.classList.toggle("active", isActive);
        panel.hidden = !isActive;
    });

    document.querySelectorAll("[data-changelog-tab]").forEach((button) => {
        const isActive = button.dataset.changelogTab === tabName;
        button.classList.toggle("active", isActive);
        button.setAttribute("aria-selected", isActive ? "true" : "false");
        button.tabIndex = isActive ? 0 : -1;
    });

    if (sourceButton instanceof HTMLElement) {
        sourceButton.focus({ preventScroll: true });
    }

    if (tabName === "global-changelog") {
        loadGlobalChangelog();
    } else if (tabName === "version-changelog") {
        populateVersionSelect();
        loadVersionChangelog();
    } else if (tabName === "history") {
        loadVersionHistory();
    }
}

function switchChangelogTab(e, tabName) {
    if (e) {
        e.preventDefault();
    }

    const sourceButton = e?.currentTarget instanceof HTMLElement
        ? e.currentTarget
        : document.querySelector(`[data-changelog-tab="${tabName}"]`);

    activateChangelogTab(tabName, sourceButton);
}

/**
 * Загружает глобальный changelog
 */
async function loadGlobalChangelog() {
    const contentDiv = document.getElementById("global-changelog-content");
    if (!contentDiv) return;

    contentDiv.textContent = t("loading");

    try {
        const response = await fetch(`${API_BASE}/changelog`);

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const text = await response.text();
        if (!text.trim()) {
            contentDiv.textContent = t("globalChangelogNotAvailable");
            return;
        }

        contentDiv.innerHTML = "";
        const pre = document.createElement("pre");
        pre.textContent = safeText(text);
        contentDiv.appendChild(pre);
    } catch (e) {
        console.error("Error loading global changelog:", e);
        contentDiv.textContent = `${t("errorLoadingChangelog")}: ${safeText(e.message)}`;
    }
}

/**
 * Загружает changelog для конкретной версии
 */
async function loadVersionChangelog() {
    const select = document.getElementById("version-select");
    const version = select?.value || "";

    const contentDiv = document.getElementById("version-changelog-content");
    if (!contentDiv) return;

    contentDiv.textContent = t("loading");

    try {
        const response = await fetch(`${API_BASE}/changelog/${version}`);

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const text = await response.text();
        if (!text.trim()) {
            contentDiv.textContent = `${t("changelogNotAvailableForVersion")} ${safeText(version)}`;
            return;
        }

        contentDiv.innerHTML = "";
        const pre = document.createElement("pre");
        pre.textContent = safeText(text);
        contentDiv.appendChild(pre);
    } catch (e) {
        console.error("Error loading version changelog:", e);
        contentDiv.textContent = `${t("errorLoadingChangelog")}: ${safeText(e.message)}`;
    }
}

/**
 * Загружает историю версий
 */
async function loadVersionHistory() {
    const contentDiv = document.getElementById("history-list");
    if (!contentDiv) return;

    contentDiv.innerHTML = "";
    const loadingRow = document.createElement("tr");
    const loadingCell = document.createElement("td");
    loadingCell.colSpan = 4;
    loadingCell.style.textAlign = "center";
    loadingCell.style.color = "#999";
    loadingCell.textContent = t("loading");
    loadingRow.appendChild(loadingCell);
    contentDiv.appendChild(loadingRow);

    try {
        const response = await fetch(`${API_BASE}/versions/history?take=50`);

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const data = await response.json();

        contentDiv.innerHTML = "";

        if (!data.data || data.data.length === 0) {
            const emptyRow = document.createElement("tr");
            const emptyCell = document.createElement("td");
            emptyCell.colSpan = 4;
            emptyCell.style.textAlign = "center";
            emptyCell.style.color = "#999";
            emptyCell.textContent = t("noHistoryAvailable");
            emptyRow.appendChild(emptyCell);
            contentDiv.appendChild(emptyRow);
            return;
        }

        const fragment = document.createDocumentFragment();
        data.data.forEach((log) => {
            const row = document.createElement("tr");

            const timestamp = document.createElement("td");
            timestamp.textContent = new Date(log.timestamp).toLocaleString();
            row.appendChild(timestamp);

            const v6 = document.createElement("td");
            const v6Strong = document.createElement("strong");
            v6Strong.textContent = safeText(log.v6Stable || "-");
            v6.appendChild(v6Strong);
            row.appendChild(v6);

            const v7Fixed = document.createElement("td");
            v7Fixed.textContent = safeText(log.v7Fixed || "-");
            row.appendChild(v7Fixed);

            const v7Stable = document.createElement("td");
            v7Stable.textContent = safeText(log.v7Stable || "-");
            row.appendChild(v7Stable);

            fragment.appendChild(row);
        });

        contentDiv.appendChild(fragment);
    } catch (e) {
        console.error("Error loading version history:", e);
        contentDiv.innerHTML = "";
        const errorRow = document.createElement("tr");
        const errorCell = document.createElement("td");
        errorCell.colSpan = 4;
        errorCell.style.textAlign = "center";
        errorCell.style.color = "#d32f2f";
        errorCell.textContent = `${t("error")}: ${safeText(e.message)}`;
        errorRow.appendChild(errorCell);
        contentDiv.appendChild(errorRow);
    }
}

/**
 * Обновляет выпадающий список версий при загрузке вкладки versions
 */
function populateVersionSelect() {
    const select = document.getElementById("version-select");
    if (!select) return;

    const allVersions = [];

    // Собираем все версии v6
    const v6ListRows = document.querySelectorAll("#v6-list tr");
    v6ListRows.forEach((row) => {
        const versionCell = row.querySelector("td:nth-child(2) strong");
        if (versionCell) {
            allVersions.push(versionCell.textContent);
        }
    });

    // Собираем все версии v7
    const v7ListRows = document.querySelectorAll("#v7-list tr");
    v7ListRows.forEach((row) => {
        const versionCell = row.querySelector("td:nth-child(2) strong");
        if (versionCell) {
            const version = versionCell.textContent;
            if (!allVersions.includes(version)) {
                allVersions.push(version);
            }
        }
    });

    // Сортируем в обратном порядке (новые в начале)
    allVersions.sort().reverse();

    // Обновляем select
    const currentValue = select.value;
    select.innerHTML = `<option value="">${safeHtml(t("selectVersion"))}</option>`;
    allVersions.forEach((v) => {
        const option = document.createElement("option");
        option.value = v;
        option.textContent = v;
        select.appendChild(option);
    });
    select.value = currentValue; // Восстанавливаем предыдущее значение если оно есть
}

/**
 * Вспомогательная функция для экранирования HTML
 */
function escapeHtml(text) {
    const value = safeText(text);
    const map = {
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        '"': "&quot;",
        "'": "&#039;"
    };
    return value.replace(/[&<>"']/g, (m) => map[m]);
}

function safeHtml(value) {
    return escapeHtml(safeText(value));
}

/**
 * Останавливает периодические обновления (для cleanup)
 */
function stopPeriodicUpdates() {
    if (timers.status) {
        clearInterval(timers.status);
        timers.status = null;
    }
    if (timers.versions) {
        clearInterval(timers.versions);
        timers.versions = null;
    }
}

function getMainTabLink(tabName) {
    return document.querySelector(`.nav-link[data-tab="${tabName}"]`);
}

function activateMainTab(tabName, sourceLink = null) {
    if (!MAIN_TABS.includes(tabName)) return;

    document.querySelectorAll(".tab-pane").forEach((pane) => {
        const isActive = pane.id === tabName;
        pane.classList.toggle("active", isActive);
        pane.hidden = !isActive;
    });

    document.querySelectorAll(".nav-link[data-tab]").forEach((link) => {
        const isActive = link.dataset.tab === tabName;
        link.classList.toggle("active", isActive);
        link.setAttribute("aria-selected", isActive ? "true" : "false");
        link.tabIndex = isActive ? 0 : -1;
    });

    const linkToFocus =
        sourceLink?.matches?.(".nav-link[data-tab]")
            ? sourceLink
            : getMainTabLink(tabName);
    if (linkToFocus instanceof HTMLElement) {
        linkToFocus.focus({ preventScroll: true });
    }

    updateBreadcrumb([
        { label: `🏠 ${t("home")}`, tab: "dashboard" },
        { label: TAB_LABELS[tabName] || tabName }
    ]);

    if (tabName === "dashboard") {
        loadDashboard();
        loadTodayClientUpdates(true);
    } else if (tabName === "logs") {
        loadLogs();
    } else if (tabName === "schedule") {
        loadSchedule();
        if (scheduleUpdateInterval) clearInterval(scheduleUpdateInterval);
        scheduleUpdateInterval = setInterval(loadSchedule, 60000);
    } else {
        if (scheduleUpdateInterval) {
            clearInterval(scheduleUpdateInterval);
            scheduleUpdateInterval = null;
        }

        if (tabName === "config") {
            loadAllowedArches();
            loadV7Packages();
            loadDeletePrefixes();
            loadPointerRouting(true);
        }
    }
}

function switchTab(e, tabName) {
    if (e) {
        e.preventDefault();
    }

    const sourceLink =
        e?.target?.closest?.(".nav-link[data-tab], .breadcrumb-item[data-tab]") ||
        getMainTabLink(tabName);

    activateMainTab(tabName, sourceLink);
    closeSidebarOnMobile();
}

function setPointerBranchSummary(branchVersions, upstreamBranchVersions) {
    const v6El = document.getElementById("pointer-branch-v6");
    const v7FixedEl = document.getElementById("pointer-branch-v7fixed");
    const v7LatestEl = document.getElementById("pointer-branch-v7latest");

    const resolveValue = (localVersion, upstreamInfo) => {
        const upstreamValue = formatVersionWithBuild(upstreamInfo?.version, upstreamInfo?.build);
        if (upstreamValue !== "-") return upstreamValue;
        return safeText(localVersion || "-");
    };

    if (v6El) v6El.textContent = resolveValue(branchVersions?.v6, upstreamBranchVersions?.v6);
    if (v7FixedEl) v7FixedEl.textContent = resolveValue(branchVersions?.v7Fixed, upstreamBranchVersions?.v7Fixed);
    if (v7LatestEl) v7LatestEl.textContent = resolveValue(branchVersions?.v7Latest, upstreamBranchVersions?.v7Latest);
}

function renderPointerRoutingError(message) {
    const tbody = document.getElementById("pointer-routing-list");
    if (!tbody) return;

    tbody.innerHTML = "";
    const row = document.createElement("tr");
    const cell = document.createElement("td");
    cell.colSpan = 5;
    cell.textContent = safeText(message);
    cell.style.textAlign = "center";
    cell.style.color = "var(--text-secondary)";
    row.appendChild(cell);
    tbody.appendChild(row);
}

function renderPointerRouting(data) {
    const tbody = document.getElementById("pointer-routing-list");
    if (!tbody) return;

    tbody.innerHTML = "";

    const rows = Array.isArray(data?.rows) ? data.rows : [];
    const options = Array.isArray(data?.branchOptions) && data.branchOptions.length > 0
        ? data.branchOptions
        : [
            { value: "v6", label: "v6" },
            { value: "v7Fixed", label: "v7Fixed" },
            { value: "v7Latest", label: "v7Latest" }
        ];

    setPointerBranchSummary(data?.branchVersions || {}, data?.upstreamBranchVersions || {});

    if (rows.length === 0) {
        renderPointerRoutingError(t("noPointerRoutingData"));
        return;
    }

    const fragment = document.createDocumentFragment();

    rows.forEach((item) => {
        const row = document.createElement("tr");

        const pointerCell = document.createElement("td");
        pointerCell.textContent = safeText(item.pointer || "-");
        row.appendChild(pointerCell);

        const activeBranchCell = document.createElement("td");
        const controls = document.createElement("div");
        controls.className = "pointer-route-controls";

        const select = document.createElement("select");
        select.className = "pointer-route-select";
        select.dataset.pointer = safeText(item.pointer || "");

        options.forEach((opt) => {
            const option = document.createElement("option");
            option.value = safeText(opt.value || "");
            option.textContent = safeText(opt.label || opt.value || "");
            select.appendChild(option);
        });

        const selectedBranch = safeText(item.activeBranch || item.defaultBranch || "v6");
        select.value = selectedBranch;

        const applyButton = document.createElement("button");
        applyButton.type = "button";
        applyButton.className = "btn btn-sm btn-primary pointer-route-apply-btn";
        applyButton.dataset.pointer = safeText(item.pointer || "");
        applyButton.textContent = t("applyRoute");

        controls.appendChild(select);
        controls.appendChild(applyButton);
        activeBranchCell.appendChild(controls);
        row.appendChild(activeBranchCell);

        const defaultBranchCell = document.createElement("td");
        defaultBranchCell.textContent = safeText(item.defaultBranch || "-");
        row.appendChild(defaultBranchCell);

        const servedCell = document.createElement("td");
        servedCell.textContent = formatVersionWithBuild(item.servedVersion, item.servedBuild);
        row.appendChild(servedCell);

        const upstreamCell = document.createElement("td");
        upstreamCell.textContent = item.upstreamAvailable
            ? formatVersionWithBuild(item.upstreamVersion, item.upstreamBuild)
            : t("upstreamUnavailable");
        row.appendChild(upstreamCell);

        fragment.appendChild(row);
    });

    tbody.appendChild(fragment);
}

async function savePointerRoute(pointer, branch, rowElement) {
    if (!pointer || !branch) return;

    const select = rowElement?.querySelector(".pointer-route-select");
    const applyButton = rowElement?.querySelector(".pointer-route-apply-btn");

    if (select) select.disabled = true;
    if (applyButton) applyButton.disabled = true;

    try {
        const response = await fetch(`${API_BASE}/settings/pointers/route`, {
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify({ pointer, branch })
        });

        if (!response.ok) {
            let errorMessage = `HTTP ${response.status}`;
            try {
                const payload = await response.json();
                errorMessage = safeText(payload.message || payload.code || errorMessage);
            } catch {
                // ignore parse errors, keep HTTP status
            }
            throw new Error(errorMessage);
        }

        showToast(`✓ ${t("pointerRoutingSaved")}: ${pointer} -> ${branch}`, "success");

        const status = document.getElementById("pointer-routing-status");
        if (status) {
            status.textContent = `${t("pointerRoutingSaved")}: ${pointer} -> ${branch}`;
            status.style.color = "var(--success)";
        }

        await loadPointerRouting(true);
    } catch (e) {
        console.error("Error saving pointer route:", e);
        showToast(`${t("pointerRoutingSaveFailed")}: ${e.message}`, "error");
    } finally {
        if (select) select.disabled = false;
        if (applyButton) applyButton.disabled = false;
    }
}

async function loadPointerRouting(force = false, showErrors = false) {
    const tbody = document.getElementById("pointer-routing-list");
    if (!tbody) return;

    const activePaneId = document.querySelector(".tab-pane.active")?.id;
    if (!force && activePaneId !== "config") {
        return;
    }

    const now = Date.now();
    if (!force && now - lastPointerRoutingRefresh < POINTER_ROUTING_REFRESH_MS) {
        return;
    }

    showLoading("pointer-routing-list", t("loadingPointerRouting"));

    try {
        const response = await fetch(`${API_BASE}/settings/pointers`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const data = await response.json();
        renderPointerRouting(data);
        lastPointerRoutingRefresh = Date.now();

        const status = document.getElementById("pointer-routing-status");
        if (status) {
            const count = Array.isArray(data?.rows) ? data.rows.length : 0;
            status.textContent = `${t("pointerRoutingLoaded")}: ${count}`;
            status.style.color = "var(--text-secondary)";
        }
    } catch (e) {
        console.error("Error loading pointer routing:", e);
        renderPointerRoutingError(t("pointerRoutingLoadFailed"));
        setPointerBranchSummary({}, {});

        const status = document.getElementById("pointer-routing-status");
        if (status) {
            status.textContent = `${t("pointerRoutingLoadFailed")}: ${e.message}`;
            status.style.color = "var(--danger)";
        }

        if (showErrors) {
            showToast(`${t("pointerRoutingLoadFailed")}: ${e.message}`, "error");
        }
    }
}

async function loadAllowedArches() {
    const container = document.getElementById("arches-container");
    if (!container) return;

    try {
        const response = await fetch(`${API_BASE}/settings/arches`);
        if (!response.ok) {
            // Если эндпоинта нет – тихо выходим, чтобы UI не ломался
            console.warn("Failed to load allowed arches:", response.status);
            return;
        }

        const arches = await response.json();
        const set = new Set(arches.map((a) => a.toLowerCase()));

        container.querySelectorAll('input[type="checkbox"]').forEach((cb) => {
            cb.checked = set.has(cb.value.toLowerCase());
        });

        const status = document.getElementById("arches-status");
        if (status) {
            status.textContent =
                arches.length > 0
                ? `${t("loaded")}: ${arches.join(", ")}`
                : t("loadedDefaultArchitectures");
        }
    } catch (e) {
        console.error("Error loading allowed arches:", e);
        showToast(`${t("errorLoadingArchitectures")}: ${e.message}`, "error");
    }
}

async function saveAllowedArches() {
    const container = document.getElementById("arches-container");
    if (!container) return;

    const selected = Array.from(
        container.querySelectorAll('input[type="checkbox"]:checked')
    ).map((cb) => cb.value);

    // Можно предупредить, если вообще всё сняли
    if (selected.length === 0) {
        if (
            !confirm(
                t("noArchitecturesSelectedConfirm")
            )
        ) {
            return;
        }
    }

    try {
        const response = await fetch(`${API_BASE}/settings/arches`,
            {
                method: "POST",
                headers: {
                    "Content-Type": "application/json"
                },
                body: JSON.stringify(selected)
            });

        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        showToast(t("allowedArchitecturesSaved"), "success");

        const status = document.getElementById("arches-status");
        if (status) {
            status.textContent =
                selected.length > 0
                ? `${t("saved")}: ${selected.join(", ")}`
                : t("savedDefaultArchitectures");
        }
    } catch (e) {
        console.error("Error saving allowed arches:", e);
        showToast(`${t("errorSavingArchitectures")}: ${e.message}`, "error");
    }
}

async function loadV7Packages() {
    const container = document.getElementById("v7-packages-container");
    if (!container) return;

    try {
        const response = await fetch(`${API_BASE}/settings/v7-packages`);
        if (!response.ok) {
            console.warn("Failed to load v7 packages:", response.status);
            return;
        }

        const packages = await response.json();
        const set = new Set(packages.map((p) => p.toLowerCase()));

        container.querySelectorAll('input[type="checkbox"]').forEach((cb) => {
            cb.checked = set.has(cb.value.toLowerCase());
        });

        const status = document.getElementById("v7-packages-status");
        if (status) {
            status.textContent =
                packages.length > 0
                ? `${t("loaded")}: ${packages.map(translateV7Package).join(", ")}`
                : t("noPackagesSelected");
        }
    } catch (e) {
        console.error("Error loading v7 packages:", e);
        showToast(`${t("errorLoadingV7Packages")}: ${e.message}`, "error");
    }
}

async function saveV7Packages() {
    const container = document.getElementById("v7-packages-container");
    if (!container) return;

    const selected = Array.from(
        container.querySelectorAll('input[type="checkbox"]:checked')
    ).map((cb) => cb.value);

    try {
        const response = await fetch(`${API_BASE}/settings/v7-packages`,
            {
                method: "POST",
                headers: {
                    "Content-Type": "application/json"
                },
                body: JSON.stringify(selected)
            });

        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        showToast(t("v7PackagesSaved"), "success");

        const status = document.getElementById("v7-packages-status");
        if (status) {
            status.textContent =
                selected.length > 0
                ? `${t("saved")}: ${selected.map(translateV7Package).join(", ")}`
                : t("savedNoPackagesSelected");
        }
    } catch (e) {
        console.error("Error saving v7 packages:", e);
        showToast(`${t("errorSavingV7Packages")}: ${e.message}`, "error");
    }
}

function translateV7Package(packageName) {
    const key = packageName.replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
    const translated = t(key);
    return translated === key ? packageName : translated;
}

async function loadDeletePrefixes() {
    try {
        const response = await fetch(`${API_BASE}/settings/delete-prefixes`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const data = await response.json();
        const textarea = document.getElementById("delete-prefixes-textarea");
        if (textarea) {
            textarea.value = JSON.stringify(data, null, 2);
        }
        const status = document.getElementById("prefixes-status");
        if (status) {
            status.textContent = `${t("loaded")} ${data.length || 0} ${t("prefixes")}`;
            status.style.color = data.length > 0 ? "var(--success)" : "var(--text-secondary)";
        }
    } catch (e) {
        console.error("Error loading delete prefixes:", e);
        showToast(`${t("errorLoadingDeletePrefixes")}: ${e.message}`, "error");
    }
}

async function saveDeletePrefixes() {
    const textarea = document.getElementById("delete-prefixes-textarea");
    if (!textarea) return;

    try {
        const content = textarea.value.trim();
        if (!content) {
            showToast(t("pleaseEnterJsonData"), "warning");
            return;
        }

        const data = JSON.parse(content);
        if (!Array.isArray(data)) {
            throw new Error(t("mustBeJsonArray"));
        }

        // Отправляем на сервер для сохранения
        const response = await fetch(`${API_BASE}/settings/delete-prefixes`, {
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify(data)
        });

        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const result = await response.json();
        showToast(t("deletePrefixesSavedSuccessfully"), "success");
        const status = document.getElementById("prefixes-status");
        if (status) {
            status.textContent = `${t("saved")} ${result.count || data.length} ${t("prefixes")}`;
            status.style.color = "var(--success)";
        }
    } catch (e) {
        console.error("Error saving delete prefixes:", e);
        showToast(`${t("error")}: ${e.message}`, "error");
    }
}

function switchVersionTab(e, tabName) {
    if (e) {
        e.preventDefault();
    }

    const sourceButton = e?.currentTarget instanceof HTMLElement
        ? e.currentTarget
        : document.querySelector(`[data-version-tab="${tabName}"]`);

    activateVersionTab(tabName, sourceButton);
}

function activateVersionTab(tabName, sourceButton = null) {
    document.querySelectorAll(".version-tab").forEach((panel) => {
        const isActive = panel.id === tabName;
        panel.classList.toggle("active", isActive);
        panel.hidden = !isActive;
    });

    document.querySelectorAll("[data-version-tab]").forEach((button) => {
        const isActive = button.dataset.versionTab === tabName;
        button.classList.toggle("active", isActive);
        button.setAttribute("aria-selected", isActive ? "true" : "false");
        button.tabIndex = isActive ? 0 : -1;
    });

    if (sourceButton instanceof HTMLElement) {
        sourceButton.focus({ preventScroll: true });
    }
}

function renderTodayClientUpdates(rows) {
    const tbody = document.getElementById("today-clients-list");
    if (!tbody) return;

    tbody.innerHTML = "";

    if (!Array.isArray(rows) || rows.length === 0) {
        const emptyRow = document.createElement("tr");
        const emptyCell = document.createElement("td");
        emptyCell.colSpan = 5;
        emptyCell.textContent = t("noClientUpdatesToday");
        emptyCell.style.textAlign = "center";
        emptyCell.style.color = "var(--text-secondary)";
        emptyRow.appendChild(emptyCell);
        tbody.appendChild(emptyRow);
        return;
    }

    const fragment = document.createDocumentFragment();

    rows.forEach((item) => {
        const row = document.createElement("tr");

        const ipCell = document.createElement("td");
        ipCell.textContent = safeText(item.clientIp || "unknown");
        row.appendChild(ipCell);

        const versionCell = document.createElement("td");
        versionCell.textContent = safeText(item.version || "-");
        row.appendChild(versionCell);

        const fileCell = document.createElement("td");
        fileCell.textContent = safeText(item.file || "-");
        row.appendChild(fileCell);

        const requestsCell = document.createElement("td");
        const requests = Number(item.requests);
        requestsCell.textContent = Number.isFinite(requests)
            ? requests.toLocaleString()
            : safeText(item.requests || "0");
        row.appendChild(requestsCell);

        const lastSeenCell = document.createElement("td");
        lastSeenCell.textContent = item.lastSeen
            ? formatDateTime(item.lastSeen)
            : "-";
        row.appendChild(lastSeenCell);

        fragment.appendChild(row);
    });

    tbody.appendChild(fragment);
}

async function loadTodayClientUpdates(force = false) {
    const activePaneId = document.querySelector(".tab-pane.active")?.id;
    if (!force && activePaneId !== "dashboard") {
        return;
    }

    const now = Date.now();
    if (!force && now - lastClientActivityRefresh < CLIENT_ACTIVITY_REFRESH_MS) {
        return;
    }

    const tbody = document.getElementById("today-clients-list");
    if (!tbody) return;

    try {
        const response = await fetch(`${API_BASE}/dashboard/clients-today?take=20`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const data = await response.json();
        renderTodayClientUpdates(data.data || []);
        lastClientActivityRefresh = Date.now();
    } catch (error) {
        console.error("Error loading today client updates:", error);
        renderTodayClientUpdates([]);
    }
}

async function loadDashboard() {
    try {
        const response = await fetch(`${API_BASE}/status`);
        const data = await response.json();

        updateStatusWithPulse("server-status", `🟢 ${t("online")}`);
        updateStatusWithPulse("last-check",
            data.lastCheck
            ? new Date(data.lastCheck).toLocaleString()
            : t("pending"));
        updateStatusWithPulse(
            "uptime",
            formatUptime(data.uptime)
        );
        updateStatusWithPulse("memory", data.process.memory);

        // Исправленное отображение потоков
        if (typeof data.process.threads === "object") {
            updateStatusWithPulse(
                "threads",
                `${data.process.threads.threadPoolActive}/${data.process.threads.maxWorkerThreads}`
            );
        } else {
            updateStatusWithPulse("threads", data.process.threads);
        }

        updateStatusWithPulse("cpuUsage", data.process.cpuUsage);
        const diskElem = document.getElementById("disk");
        if (diskElem) {
            updateStatusWithPulse("disk",
                data.diskUsage
                ? `${data.diskUsage.totalGB} GB`
                : "-");
        }

        const totalFiles = Number(data.downloads?.files);
        updateStatusWithPulse(
            "total-files",
            Number.isFinite(totalFiles) ? totalFiles.toLocaleString() : safeText(data.downloads?.files)
        );

        const totalGb = Number(data.downloads?.totalGb ?? data.downloads?.total);
        updateStatusWithPulse("total-gb", formatGigabytes(totalGb));
        applyTotalGbGradient(totalGb);
        renderCurrentRosDownload(data.downloads?.activity);
        await loadTlsDiagnostics();

        loadTodayClientUpdates();
    } catch (e) {
        console.error("Failed to load dashboard:", e);
        updateStatusWithPulse("server-status", `🔴 ${t("offline")}`);
        renderCurrentRosDownload(null);
        renderTlsDiagnostics({
            isSuccess: false,
            failureCategory: "dashboard_offline",
            message: safeText(e?.message || t("dashboardRequestFailed")),
            recommendation: t("tlsDiagnosticsUnavailable")
        });
    }
}

async function loadVersions() {
    try {
        showLoading("v6-list", t("loadingV6Versions"));
        showLoading("v7-list", t("loadingV7Versions"));

        const response = await fetch(`${API_BASE}/versions`);
        const data = await response.json();

        document.getElementById("v6-active").textContent = data.v6.active || "-";
        document.getElementById("v7-fixed").textContent =
            data.v7.activeFixed || "-";
        document.getElementById("v7-latest").textContent =
            data.v7.activeLatest || "-";

        updateTable("v6", data.v6.versions, data.v6.active);
        updateTable(
            "v7",
            data.v7.versions,
            data.v7.activeFixed,
            data.v7.activeLatest
        );
        refreshSelectedVersionsCount();
    } catch (e) {
        console.error("Error loading versions:", e);
        showToast(`${t("errorLoadingVersions")}: ${e.message}`, "error");
    }
}

function normalizeVersionEntry(entry) {
    if (typeof entry === "string") {
        return {
            version: safeText(entry),
            architectures: [],
            files: 0
        };
    }

    return {
        version: safeText(entry?.version ?? entry?.Version ?? ""),
        architectures: Array.isArray(entry?.architectures)
            ? entry.architectures.map((x) => safeText(x)).filter(Boolean)
            : [],
        files: Number(entry?.files || 0)
    };
}

function updateTable(branch, versions, ...active) {
    const tbody = document.getElementById(`${branch}-list`);
    if (!tbody) return;

    tbody.innerHTML = "";

    versions.map(normalizeVersionEntry).forEach((entry) => {
        const version = safeText(entry.version);
        if (!version) return;

        const isActive = active.includes(version);
        const row = document.createElement("tr");
        row.dataset.version = version;

        const selectCell = document.createElement("td");
        selectCell.className = "version-select-cell";
        const selectCheckbox = document.createElement("input");
        selectCheckbox.type = "checkbox";
        selectCheckbox.className = "version-select-checkbox";
        selectCheckbox.dataset.version = version;
        selectCheckbox.dataset.branch = branch;
        selectCheckbox.disabled = isActive;
        selectCell.appendChild(selectCheckbox);
        row.appendChild(selectCell);
        row.dataset.branch = branch;

        const versionCell = document.createElement("td");
        const versionStrong = document.createElement("strong");
        versionStrong.textContent = version;
        versionCell.appendChild(versionStrong);
        row.appendChild(versionCell);

        const archCell = document.createElement("td");
        if (entry.architectures.length > 0) {
            const archContainer = document.createElement("div");
            archContainer.className = "version-architectures";

            entry.architectures.forEach((arch) => {
                const chip = document.createElement("span");
                chip.className = "version-arch-chip";
                chip.textContent = arch;
                archContainer.appendChild(chip);
            });

            archCell.appendChild(archContainer);
        } else {
            archCell.textContent = "-";
        }

        if (entry.files > 0) {
            const filesMeta = document.createElement("div");
            filesMeta.className = "version-files-meta";
            filesMeta.textContent = `${t("files")}: ${entry.files}`;
            archCell.appendChild(filesMeta);
        }

        row.appendChild(archCell);

        if (branch === "v7") {
            const isFixed = version === active[0];
            const isLatest = version === active[1];
            const type = isFixed ? t("fixed") : isLatest ? t("latest") : "";
            const typeCell = document.createElement("td");
            typeCell.textContent = type;
            row.appendChild(typeCell);
        }

        const statusCell = document.createElement("td");
        const statusBadge = document.createElement("span");
        statusBadge.className = `status-badge ${isActive ? "active" : "inactive"}`;
        statusBadge.textContent = isActive ? "✓" : "✗";
        statusCell.appendChild(statusBadge);
        row.appendChild(statusCell);

        const actionCell = document.createElement("td");
        const setButton = document.createElement("button");
        setButton.className = "btn-set";
        setButton.type = "button";
        setButton.textContent = t("set");
        setButton.setAttribute("aria-label", `${t("setVersionAsActive")} ${version}`);
        setButton.addEventListener("click", () => setVersion(version));
        actionCell.appendChild(setButton);

        if (!isActive) {
            const removeButton = document.createElement("button");
            removeButton.className = "btn-delete";
            removeButton.type = "button";
            removeButton.textContent = t("delete");
            removeButton.setAttribute("aria-label", `${t("removeVersion")} ${version}`);
            removeButton.addEventListener("click", () => removeVersion(version, branch));
            actionCell.appendChild(removeButton);
        }

        row.appendChild(actionCell);
        tbody.appendChild(row);
    });
}

async function setVersion(v) {
    try {
        showToast(`${t("settingVersion")} ${v}...`, "info");
        const response = await fetch(`${API_BASE}/set-active-version/${v}`,
            {
                method: "POST"
            });

        if (!response.ok) {
            const error = await response.json();
            showToast(`${t("error")}: ${error.message || error.code}`, "error");
            console.error("Set version error:", error);
            return;
        }

        showToast(`${t("versionSetAsActive")} ${v}`, "success");
        await loadVersions();
    } catch (e) {
        console.error("Set version error:", e);
        showToast(`${t("error")}: ${e.message}`, "error");
    }
}

async function removeVersion(v, branch = "") {
    if (!confirm(`${t("deleteVersionConfirm")} ${v}?`)) return;

    try {
        showToast(`${t("deletingVersion")} ${v}...`, "warning");
        const result = await deleteVersionRequest(v, branch);
        if (!result.success) {
            showToast(`${t("error")}: ${result.message}`, "error");
            return;
        }

        showToast(`${t("versionDeleted")} ${v}`, "success");
        await loadVersions();
    } catch (e) {
        console.error("Remove version error:", e);
        showToast(`${t("error")}: ${e.message}`, "error");
    }
}

async function deleteVersionRequest(version, branch = "") {
    try {
        const query = branch ? `?branch=${encodeURIComponent(branch)}` : "";
        const response = await fetch(`${API_BASE}/remove-version/${encodeURIComponent(version)}${query}`, {
            method: "DELETE"
        });

        if (!response.ok) {
            const error = await response.json().catch(() => ({}));
            return {
                success: false,
                message: error.message || error.code || `HTTP ${response.status}`
            };
        }

        return {success: true};
    } catch (error) {
        return {
            success: false,
            message: safeText(error?.message || "network_error")
        };
    }
}

function getSelectedVersions() {
    const checked = document.querySelectorAll(".version-select-checkbox:checked");
    const selected = [];
    const unique = new Set();
    checked.forEach((cb) => {
        const version = safeText(cb.dataset.version);
        const branch = safeText(cb.dataset.branch);
        if (!version) return;

        const key = `${branch}:${version}`;
        if (unique.has(key)) return;
        unique.add(key);
        selected.push({ version, branch });
    });
    return selected;
}

function refreshSelectedVersionsCount() {
    const counter = document.getElementById("selected-versions-count");
    if (!counter) return;

    const selected = getSelectedVersions();
    if (selected.length === 0) {
        counter.textContent = "";
        return;
    }

    counter.textContent = `${selected.length} ${t("selectedVersions")}`;
}

function selectVisibleVersions() {
    const activePane = document.querySelector("#versions .version-tab.active");
    if (!activePane) return;

    const checkboxes = activePane.querySelectorAll(".version-select-checkbox");
    checkboxes.forEach((cb) => {
        if (cb.disabled) return;
        const row = cb.closest("tr");
        if (!row || row.style.display === "none") return;
        cb.checked = true;
    });

    refreshSelectedVersionsCount();
}

async function deleteSelectedVersions() {
    const selected = getSelectedVersions();
    if (selected.length === 0) {
        showToast(t("noVersionsSelected"), "info");
        return;
    }

    if (!confirm(`${t("deleteSelectedConfirm")} (${selected.length})?`))
        return;

    let removed = 0;
    const failed = [];

    const groupedByBranch = new Map();
    for (const item of selected) {
        const key = safeText(item.branch).toLowerCase();
        if (!groupedByBranch.has(key)) {
            groupedByBranch.set(key, []);
        }
        groupedByBranch.get(key).push(item.version);
    }

    for (const [branch, versions] of groupedByBranch.entries()) {
        const bulk = await deleteVersionsBulkRequest(versions, branch);
        if (!bulk.success) {
            for (const version of versions) {
                const result = await deleteVersionRequest(version, branch);
                if (result.success) {
                    removed++;
                } else {
                    failed.push(`${version}: ${result.message}`);
                }
            }
            continue;
        }

        removed += bulk.deleted;
        bulk.failed.forEach((item) => {
            failed.push(`${safeText(item.version)}: ${safeText(item.reason)}`);
        });
    }

    await loadVersions();

    if (removed > 0) {
        showToast(`${t("deletedVersions")}: ${removed}`, "success");
    }

    if (failed.length > 0) {
        showToast(`${t("failedToDeleteVersions")}: ${failed.slice(0, 3).join("; ")}`, "error");
    }
}

async function deleteVersionsBulkRequest(versions, branch = "") {
    try {
        const response = await fetch(`${API_BASE}/remove-versions`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                versions,
                branch: branch || null
            })
        });

        if (!response.ok) {
            const error = await response.json().catch(() => ({}));
            return {
                success: false,
                message: error.message || error.code || `HTTP ${response.status}`
            };
        }

        const data = await response.json();
        return {
            success: true,
            deleted: Number(data.deleted || 0),
            failed: Array.isArray(data.failed) ? data.failed : []
        };
    } catch (error) {
        return {
            success: false,
            message: safeText(error?.message || "network_error")
        };
    }
}

async function checkUpdates(e) {
    const btn = e?.currentTarget || document.getElementById("check-updates-btn");
    const btnText = btn?.querySelector(".btn-text");
    if (btn) {
        btn.disabled = true;
    }
    if (btnText) {
        btnText.textContent = t("checking");
    }

    showToast(t("startingUpdateCheck"), "info");

    try {
        const response = await fetch(`${API_BASE}/update-check`,
            {
                method: "POST"
            });

        if (response.status === 409) {
            const error = await response.json();
            showToast(`⚠️ ${error.message}`, "warning");
            return;
        }

        if (response.status === 503) {
            const error = await response.json();
            await loadTlsDiagnostics(true);
            const details = safeText(error.details || error.message || t("serviceUnavailable"));
            showToast(`${t("serviceUnavailable")}: ${details}`, "error");
            return;
        }

        if (response.status === 504) {
            const error = await response.json();
            showToast(`${t("timeout")}: ${error.message}`, "error");
            return;
        }

        if (!response.ok) {
            const error = await response.json();
            showToast(`${t("error")}: ${error.message || t("failedToCheckUpdates")}`, "error");
            return;
        }

        // Успешная проверка
        const result = await response.json();
        showToast(
            `${t("updateCheckCompleted")}: ${result.downloaded} ${t("downloadedFiles")}`,
            "success"
        );

        // Обновляем данные сразу
        await new Promise((r) => setTimeout(r, 1000));
        await loadDashboard();
        await loadVersions();
    } catch (e) {
        console.error("Network error:", e);
        showToast(`${t("networkError")}: ${e.message}`, "error");
    } finally {
        if (btn) {
            btn.disabled = false;
        }
        if (btnText) {
            btnText.textContent = t("checkUpdates");
        }
    }
}

// Keyboard Shortcuts Modal
function showKeyboardShortcuts() {
    const modal = document.getElementById("shortcuts-modal");
    modal.classList.add("show");
    document.body.style.overflow = "hidden";
}

function closeShortcutsModal() {
    const modal = document.getElementById("shortcuts-modal");
    modal.classList.remove("show");
    document.body.style.overflow = "";
}

// Settings Export/Import
async function exportSettings() {
    try {
        const settings = {
            timestamp: new Date().toISOString(),
            version: "1.0",
            theme: localStorage.getItem("theme") || "dark",
            sidebarCollapsed: localStorage.getItem("sidebarCollapsed") === "true",
            schedule: {},
            config: {
                allowedArches: [],
                v7Packages: [],
                deletePrefixes: [],
                pointerRoutes: {}
            }
        };

        // Get schedule form values
        const form = document.getElementById("schedule-form");
        if (form) {
            settings.schedule = {
                enabled: form.querySelector('[name="enabled"]')?.checked ?? false,
                intervalMinutes: form.querySelector('[name="intervalMinutes"]')?.value,
                checkTime: form.querySelector('[name="checkTime"]')?.value,
                selectedDays: Array.from(form.querySelectorAll('[name="days"]:checked')).map(cb => cb.value),
                notifyOnCompletion: form.querySelector('[name="notifyOnCompletion"]')?.checked ?? true,
                notifyOnError: form.querySelector('[name="notifyOnError"]')?.checked ?? true
            };
        }

        // Get allowed architectures from API
        try {
            const archesResponse = await fetch(`${API_BASE}/settings/arches`);
            if (archesResponse.ok) {
                settings.config.allowedArches = await archesResponse.json();
            }
        } catch (err) {
            console.warn("Could not fetch allowed architectures:", err);
        }

        // Get v7 packages from API
        try {
            const v7Response = await fetch(`${API_BASE}/settings/v7-packages`);
            if (v7Response.ok) {
                settings.config.v7Packages = await v7Response.json();
            }
        } catch (err) {
            console.warn("Could not fetch v7 packages:", err);
        }

        // Get delete prefixes from API
        try {
            const prefixesResponse = await fetch(`${API_BASE}/settings/delete-prefixes`);
            if (prefixesResponse.ok) {
                settings.config.deletePrefixes = await prefixesResponse.json();
            }
        } catch (err) {
            console.warn("Could not fetch delete prefixes:", err);
        }

        // Get pointer routes from API
        try {
            const pointersResponse = await fetch(`${API_BASE}/settings/pointers`);
            if (pointersResponse.ok) {
                const pointersData = await pointersResponse.json();
                const rows = Array.isArray(pointersData?.rows) ? pointersData.rows : [];
                rows.forEach((row) => {
                    const pointer = safeText(row.pointer || "");
                    const branch = safeText(row.activeBranch || "");
                    if (pointer && branch) {
                        settings.config.pointerRoutes[pointer] = branch;
                    }
                });
            }
        } catch (err) {
            console.warn("Could not fetch pointer routes:", err);
        }

        const dataStr = JSON.stringify(settings, null, 2);
        const dataBlob = new Blob([dataStr], { type: "application/json" });
        const url = URL.createObjectURL(dataBlob);
        const link = document.createElement("a");
        link.href = url;
        link.download = `mikrotik-settings-${new Date().toISOString().split("T")[0]}.json`;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);

        showToast(t("settingsExportedSuccessfully"), "success");
    } catch (err) {
        showToast(`${t("errorExportingSettings")}: ${err.message}`, "error");
    }
}

function importSettingsClick() {
    document.getElementById("import-file").click();
}

function importSettings(event) {
    const file = event.target.files[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = async function(e) {
        try {
            const settings = JSON.parse(e.target.result);

            // Apply theme
            if (settings.theme) {
                applyTheme(settings.theme === "light" ? "light" : "dark");
            }

            // Apply sidebar state
            if (settings.sidebarCollapsed !== undefined) {
                localStorage.setItem("sidebarCollapsed", settings.sidebarCollapsed);
                const sidebar = document.getElementById("sidebar");
                if (settings.sidebarCollapsed) {
                    sidebar.classList.add("collapsed");
                } else {
                    sidebar.classList.remove("collapsed");
                }
            }

            // Apply schedule
            if (settings.schedule) {
                const form = document.getElementById("schedule-form");
                if (form) {
                    const enabled = form.querySelector('[name="enabled"]');
                    if (enabled && typeof settings.schedule.enabled === "boolean") {
                        enabled.checked = settings.schedule.enabled;
                    }

                    const intervalMinutes = settings.schedule.intervalMinutes ?? settings.schedule.interval;
                    const intervalInput = form.querySelector('[name="intervalMinutes"]');
                    if (intervalInput && intervalMinutes !== undefined && intervalMinutes !== null && intervalMinutes !== "") {
                        intervalInput.value = intervalMinutes;
                    }

                    const checkTimeInput = form.querySelector('[name="checkTime"]');
                    if (checkTimeInput && settings.schedule.checkTime) {
                        checkTimeInput.value = settings.schedule.checkTime;
                    }

                    if (settings.schedule.selectedDays && Array.isArray(settings.schedule.selectedDays)) {
                        form.querySelectorAll('[name="days"]').forEach(cb => {
                            cb.checked = settings.schedule.selectedDays.includes(cb.value);
                        });
                    }

                    const notifyCompletion = form.querySelector('[name="notifyOnCompletion"]');
                    if (notifyCompletion && typeof settings.schedule.notifyOnCompletion === "boolean") {
                        notifyCompletion.checked = settings.schedule.notifyOnCompletion;
                    }

                    const notifyError = form.querySelector('[name="notifyOnError"]');
                    if (notifyError && typeof settings.schedule.notifyOnError === "boolean") {
                        notifyError.checked = settings.schedule.notifyOnError;
                    }
                }
            }

            // Apply architectures
            if (settings.config?.allowedArches && Array.isArray(settings.config.allowedArches)) {
                document.querySelectorAll('#arches-container input[type="checkbox"]').forEach(cb => {
                    cb.checked = settings.config.allowedArches.includes(cb.value);
                });
                // Save architectures via API
                try {
                    await fetch(`${API_BASE}/settings/arches`, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify(settings.config.allowedArches)
                    });
                } catch (err) {
                    console.warn("Could not save architectures:", err);
                }
            }

            // Apply v7 packages
            if (settings.config?.v7Packages && Array.isArray(settings.config.v7Packages)) {
                document.querySelectorAll('#v7-packages-container input[type="checkbox"]').forEach(cb => {
                    cb.checked = settings.config.v7Packages.includes(cb.value);
                });
                // Save v7 packages via API
                try {
                    await fetch(`${API_BASE}/settings/v7-packages`, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify(settings.config.v7Packages)
                    });
                } catch (err) {
                    console.warn("Could not save v7 packages:", err);
                }
            }

            // Apply delete prefixes
            if (settings.config?.deletePrefixes && Array.isArray(settings.config.deletePrefixes)) {
                // Save delete prefixes via API
                try {
                    await fetch(`${API_BASE}/settings/delete-prefixes`, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify(settings.config.deletePrefixes)
                    });
                } catch (err) {
                    console.warn("Could not save delete prefixes:", err);
                }
            }

            // Apply pointer routes
            if (settings.config?.pointerRoutes && typeof settings.config.pointerRoutes === "object") {
                const pointerRoutes = Object.entries(settings.config.pointerRoutes)
                    .filter(([pointer, branch]) => typeof pointer === "string" && typeof branch === "string");

                for (const [pointer, branch] of pointerRoutes) {
                    try {
                        await fetch(`${API_BASE}/settings/pointers/route`, {
                            method: "POST",
                            headers: { "Content-Type": "application/json" },
                            body: JSON.stringify({ pointer, branch })
                        });
                    } catch (err) {
                        console.warn(`Could not save pointer route ${pointer}:`, err);
                    }
                }
            }

            await loadPointerRouting(true);

            showToast(t("settingsImportedSuccessfully"), "success");
            document.getElementById("import-file").value = ""; // Reset input
        } catch (err) {
            showToast(`${t("errorImportingSettings")}: ${err.message}`, "error");
        }
    };
    reader.readAsText(file);
}

// Add ? key to show shortcuts modal
document.addEventListener("keydown",
    function(e) {
        if ((e.key === "?" || e.shiftKey && e.key === "/") && !isInputActive()) {
            e.preventDefault();
            showKeyboardShortcuts();
        }
        if (e.key === "Escape") {
            closeShortcutsModal();
        }
    });

function isInputActive() {
    const activeElement = document.activeElement;
    return activeElement && ["INPUT", "TEXTAREA", "SELECT"].includes(activeElement.tagName);
}
