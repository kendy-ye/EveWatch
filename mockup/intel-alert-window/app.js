(() => {
  const { SYSTEMS, NEIGHBORHOOD, SCENARIOS, ALARM_SOUNDS, RULES, CHARACTERS, LISTENERS, CURRENT_SYSTEMS } = window.MOCK;
  const STORAGE_KEY = "evewatch.intelAlertUi.v1";
  const RULE_STORAGE_KEY = "evewatch.intelRules.v1";
  const LISTENER_STORAGE_KEY = "evewatch.intelListeners.v1";
  const ICON_UP = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m18 15-6-6-6 6"/></svg>`;
  const ICON_DOWN = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m6 9 6 6 6-6"/></svg>`;
  const ICON_TRASH = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 6h18"/><path d="M19 6v14a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6"/><path d="M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2"/></svg>`;

  const DEFAULTS = {
    enabled: true,
    view: "route",
    size: "standard",
    opacity: 100,
    autoClose: 10,
    position: "top-right",
    monitor: "primary",
    customLeft: null,
    customTop: null,
    topmost: true,
    muted: false,
    scenario: "jump3",
    batchIndex: 0,
    showAlternatives: false,
    content: {
      region: true,
      security: true,
      channel: true,
      character: true,
      sound: true,
    },
  };

  const state = loadState();
  let rules = loadRules();
  let listeners = loadListeners();
  let probeCharacterId = CHARACTERS[0]?.id ?? 0;
  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

  const stage = $("#stage");
  const alertWindow = $("#alertWindow");
  const alertHead = $("#alertHead");
  const stageEmpty = $("#stageEmpty");
  const routeViz = $("#routeViz");
  const neighborhoodViz = $("#neighborhoodViz");
  const progressBar = $("#progressBar");
  const countdownText = $("#countdownText");
  const toast = $("#toast");

  let tickId = null;
  let remaining = 0;
  let paused = false;
  let drag = null;
  let toastId = null;

  function loadState() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return structuredClone(DEFAULTS);
      const saved = JSON.parse(raw);
      return {
        ...DEFAULTS,
        ...saved,
        content: { ...DEFAULTS.content, ...(saved.content || {}) },
      };
    } catch {
      return structuredClone(DEFAULTS);
    }
  }

  function saveState() {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
    } catch {
      /* 原型可忽略存储失败 */
    }
  }

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  function normalizeRule(rule, index) {
    const min = clamp(Number(rule.min) || 0, 0, 100);
    const max = clamp(Number(rule.max) || min, min, 100);
    return {
      id: rule.id || `rule-${index}-${Date.now()}`,
      min,
      max,
      sound: ALARM_SOUNDS.some((sound) => sound.id === rule.sound) ? rule.sound : ALARM_SOUNDS[0].id,
      volume: clamp(Number(rule.volume) || 0, 0, 100),
      enabled: rule.enabled !== false,
    };
  }

  function loadRules() {
    try {
      const raw = localStorage.getItem(RULE_STORAGE_KEY);
      if (raw) {
        const saved = JSON.parse(raw);
        if (Array.isArray(saved)) {
          return saved.map(normalizeRule);
        }
      }
    } catch {
      /* 忽略损坏数据 */
    }

    return RULES.map(normalizeRule);
  }

  function saveRules() {
    try {
      localStorage.setItem(RULE_STORAGE_KEY, JSON.stringify(rules));
    } catch {
      /* 原型可忽略存储失败 */
    }
  }

  function overlaps(rule, index) {
    return rules.some((other, otherIndex) =>
      otherIndex !== index && rule.min <= other.max && other.min <= rule.max);
  }

  function renderRules() {
    const table = $("#ruleTable");
    table.innerHTML = rules.map((rule, index) => {
      const soundOptions = ALARM_SOUNDS.map((sound) =>
        `<option value="${sound.id}"${sound.id === rule.sound ? " selected" : ""}>${sound.name}</option>`).join("");
      const isOverlap = overlaps(rule, index);
      const classes = ["rule-row"];
      if (!rule.enabled) classes.push("disabled");
      if (isOverlap) classes.push("overlap");

      return `<div class="${classes.join(" ")}" data-rule-id="${rule.id}"${isOverlap ? ' title="与其它规则区间重叠，按从上到下的顺序先命中先生效"' : ""}>
        <span class="rule-order">${String(index + 1).padStart(2, "0")}</span>
        <div class="rule-range">
          <input type="number" min="0" max="100" value="${rule.min}" data-field="min" aria-label="起始跳数">
          <span>–</span>
          <input type="number" min="0" max="100" value="${rule.max}" data-field="max" aria-label="结束跳数">
          <span class="rule-unit">跳</span>
        </div>
        <select data-field="sound" aria-label="报警声音">${soundOptions}</select>
        <div class="rule-volume">
          <input type="range" min="0" max="100" value="${rule.volume}" data-field="volume" aria-label="音量">
          <span>${rule.volume}%</span>
        </div>
        <button type="button" class="switch" role="switch" aria-checked="${rule.enabled}" data-action="toggle" aria-label="启用规则"><i></i></button>
        <div class="rule-actions">
          <button type="button" class="icon-btn small" data-action="up" title="上移">${ICON_UP}</button>
          <button type="button" class="icon-btn small" data-action="down" title="下移">${ICON_DOWN}</button>
          <button type="button" class="btn btn-mini" data-action="preview">试听</button>
          <button type="button" class="icon-btn danger small" data-action="delete" title="删除">${ICON_TRASH}</button>
        </div>
      </div>`;
    }).join("");
  }

  function bindRuleEvents() {
    $("#addRuleBtn").addEventListener("click", () => {
      const last = rules[rules.length - 1];
      const min = last ? Math.min(100, Number(last.max) + 1) : 0;

      rules.push({
        id: `rule-${Date.now()}`,
        min,
        max: Math.min(100, min + 3),
        sound: ALARM_SOUNDS[0].id,
        volume: 80,
        enabled: true,
      });

      renderRules();
      saveRules();
      showToast("已添加规则");
    });

    const table = $("#ruleTable");

    table.addEventListener("change", (event) => {
      const row = event.target.closest("[data-rule-id]");
      const rule = rules.find((item) => item.id === row?.dataset.ruleId);
      if (!rule) return;

      const field = event.target.dataset.field;
      if (field === "min") {
        rule.min = clamp(Number(event.target.value) || 0, 0, 100);
        rule.max = Math.max(rule.min, rule.max);
        renderRules();
        saveRules();
      } else if (field === "max") {
        rule.max = clamp(Number(event.target.value) || rule.min, rule.min, 100);
        renderRules();
        saveRules();
      } else if (field === "sound") {
        rule.sound = event.target.value;
        saveRules();
      }
    });

    table.addEventListener("input", (event) => {
      if (event.target.dataset.field !== "volume") return;

      const row = event.target.closest("[data-rule-id]");
      const rule = rules.find((item) => item.id === row?.dataset.ruleId);
      if (!rule) return;

      rule.volume = clamp(Number(event.target.value) || 0, 0, 100);
      const output = row.querySelector(".rule-volume span");
      if (output) output.textContent = `${rule.volume}%`;
      saveRules();
    });

    table.addEventListener("click", (event) => {
      const action = event.target.closest("[data-action]");
      if (!action) return;

      const row = action.closest("[data-rule-id]");
      const index = rules.findIndex((item) => item.id === row?.dataset.ruleId);
      if (index < 0) return;

      const rule = rules[index];

      if (action.dataset.action === "toggle") {
        rule.enabled = !rule.enabled;
        renderRules();
        saveRules();
      } else if (action.dataset.action === "up" && index > 0) {
        [rules[index - 1], rules[index]] = [rules[index], rules[index - 1]];
        renderRules();
        saveRules();
      } else if (action.dataset.action === "down" && index < rules.length - 1) {
        [rules[index + 1], rules[index]] = [rules[index], rules[index + 1]];
        renderRules();
        saveRules();
      } else if (action.dataset.action === "delete") {
        rules.splice(index, 1);
        renderRules();
        saveRules();
      } else if (action.dataset.action === "preview") {
        const sound = ALARM_SOUNDS.find((item) => item.id === rule.sound);
        showToast(`试听：${sound?.name ?? rule.sound} · ${rule.volume}%（原型模拟）`);
      }
    });
  }

  function normalizeListener(item, index) {
    return {
      id: item.id || `listener-${index}-${Date.now()}`,
      characterId: Number(item.characterId) || 0,
      character: item.character || "",
      channel: item.channel || "",
      channelId: item.channelId || "",
      type: item.type || "手动输入",
      system: item.system || "位置未知",
      status: item.status || (item.channelId ? "监听中" : "等待日志"),
      enabled: item.enabled !== false,
    };
  }

  function loadListeners() {
    try {
      const raw = localStorage.getItem(LISTENER_STORAGE_KEY);
      if (raw) {
        const saved = JSON.parse(raw);
        if (Array.isArray(saved)) {
          return saved.map(normalizeListener);
        }
      }
    } catch {
      /* 忽略损坏数据 */
    }

    return LISTENERS.map(normalizeListener);
  }

  function saveListeners() {
    try {
      localStorage.setItem(LISTENER_STORAGE_KEY, JSON.stringify(listeners));
    } catch {
      /* 原型可忽略存储失败 */
    }
  }

  function renderListeners() {
    const table = $("#listenerTable");

    if (listeners.length === 0) {
      table.innerHTML = `<div class="listener-empty">还没有监听项，点击「添加监听」选择角色与频道</div>`;
      return;
    }

    table.innerHTML = listeners.map((listener) => {
      const typeClass = listener.type === "舰队频道" ? "fleet" : listener.type === "手动输入" ? "manual" : "";
      const statusText = listener.enabled ? listener.status : "已停用";
      const statusClass = !listener.enabled ? "off" : listener.status === "等待日志" ? "waiting" : "listening";
      const dynamic = listener.type === "舰队频道" ? `<span class="dynamic-tag">动态</span>` : "";

      return `<div class="listener-row${listener.enabled ? "" : " disabled"}" data-listener-id="${listener.id}">
        <div class="listener-character">${listener.character}<small>#${listener.characterId}</small></div>
        <div class="listener-channel">${listener.channel}${dynamic}<small>${listener.channelId || "手动输入，等待日志"}</small></div>
        <span class="listener-type-tag ${typeClass}">${listener.type}</span>
        <span class="listener-system">${listener.system || "位置未知"}</span>
        <span class="listener-status ${statusClass}">${statusText}</span>
        <div class="listener-actions">
          <button type="button" class="switch" role="switch" aria-checked="${listener.enabled}" data-listener-action="toggle" aria-label="启用监听"><i></i></button>
          <button type="button" class="icon-btn danger small" data-listener-action="delete" title="删除监听">${ICON_TRASH}</button>
        </div>
      </div>`;
    }).join("");
  }

  function renderProbe() {
    const select = $("#probeCharacter");
    select.innerHTML = CHARACTERS.map((character) =>
      `<option value="${character.id}"${character.id === probeCharacterId ? " selected" : ""}>${character.name}</option>`).join("");

    const character = CHARACTERS.find((item) => item.id === probeCharacterId) || CHARACTERS[0];
    if (!character) {
      $("#probeChannels").innerHTML = "";
      return;
    }

    const channels = character.channels || [];
    $("#probeChannels").innerHTML = channels.map((channel) => {
      const isLocal = channel.id === "local";
      const added = listeners.some((listener) =>
        listener.characterId === character.id
        && ((listener.channelId && channel.id && listener.channelId === channel.id) || listener.channel === channel.name));
      const disabled = isLocal || added;
      const label = isLocal ? "本地（不用于预警）" : added ? "已添加" : channel.type;

      return `<button type="button" class="channel-chip${added ? " added" : ""}"
        data-channel-id="${channel.id}" data-channel-name="${channel.name}" ${disabled ? "disabled" : ""}>
        <strong>${channel.name}</strong>
        <small>${label} · ${channel.last} · ${channel.id}</small>
      </button>`;
    }).join("");
  }

  function addListener(character, channel) {
    if (!character || !channel || channel.id === "local") {
      showToast("本地频道不参与预警");
      return;
    }

    const duplicate = listeners.some((listener) =>
      listener.characterId === character.id
      && (listener.channel === channel.name
        || (channel.id && listener.channelId === channel.id)));

    if (duplicate) {
      showToast("该频道已经在监听列表中");
      return;
    }

    const knownSystem = CURRENT_SYSTEMS[character.name] || "位置未知";
    const hasId = Boolean(channel.id);
    const status = hasId ? "监听中" : "等待日志";

    listeners.push({
      id: `listener-${Date.now()}`,
      characterId: character.id,
      character: character.name,
      channel: channel.name,
      channelId: channel.id || "",
      type: channel.type || "手动输入",
      system: hasId ? knownSystem : "位置未知",
      status,
      enabled: true,
    });

    renderListeners();
    renderProbe();
    saveListeners();
    showToast(hasId ? `已添加监听：${channel.name}` : `已添加 ${channel.name}，等待日志`);
  }

  function bindListenerEvents() {
    $("#probeCharacter").addEventListener("change", (event) => {
      probeCharacterId = Number(event.target.value);
      renderProbe();
    });

    $("#probeChannels").addEventListener("click", (event) => {
      const chip = event.target.closest("[data-channel-id]");
      if (!chip) return;

      const character = CHARACTERS.find((item) => item.id === probeCharacterId);
      if (!character) return;

      const channel = (character.channels || []).find((item) =>
        item.id === chip.dataset.channelId && item.name === chip.dataset.channelName);
      if (channel) {
        addListener(character, channel);
      }
    });

    const addManualChannel = () => {
      const input = $("#manualChannel");
      const value = input.value.trim();
      if (!value) {
        showToast("请输入频道名或 Channel ID");
        return;
      }

      const character = CHARACTERS.find((item) => item.id === probeCharacterId);
      if (!character) return;

      const matched = (character.channels || []).find((channel) =>
        channel.id.toLowerCase() === value.toLowerCase() || channel.name.toLowerCase() === value.toLowerCase());

      if (matched) {
        addListener(character, matched);
      } else {
        const isFleet = value.startsWith("fleet_");
        const isPlayer = value.startsWith("player_");
        addListener(character, {
          name: value,
          id: isFleet || isPlayer ? value : "",
          type: isFleet ? "舰队频道" : isPlayer ? "玩家频道" : "手动输入",
          last: "—",
        });
      }

      input.value = "";
    };

    $("#addManualChannelBtn").addEventListener("click", addManualChannel);
    $("#manualChannel").addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        addManualChannel();
      }
    });

    $("#rescanChannelsBtn").addEventListener("click", () => {
      renderProbe();
      showToast("已重新扫描最近 30 天聊天日志（原型模拟）");
    });

    $("#probeToggleBtn").addEventListener("click", () => {
      const probe = $("#listenerProbe");
      probe.hidden = !probe.hidden;
      if (!probe.hidden) {
        probe.scrollIntoView({ behavior: "smooth", block: "nearest" });
      }
    });

    $("#addListenerBtn").addEventListener("click", () => {
      const probe = $("#listenerProbe");
      probe.hidden = false;
      probe.scrollIntoView({ behavior: "smooth", block: "nearest" });
      $("#probeCharacter").focus();
    });

    $("#listenerTable").addEventListener("click", (event) => {
      const action = event.target.closest("[data-listener-action]");
      if (!action) return;

      const row = action.closest("[data-listener-id]");
      const index = listeners.findIndex((item) => item.id === row?.dataset.listenerId);
      if (index < 0) return;

      if (action.dataset.listenerAction === "toggle") {
        listeners[index].enabled = !listeners[index].enabled;
        renderListeners();
        saveListeners();
      } else if (action.dataset.listenerAction === "delete") {
        listeners.splice(index, 1);
        renderListeners();
        renderProbe();
        saveListeners();
      }
    });
  }

  function currentScenario() {
    return SCENARIOS[state.scenario] || SCENARIOS.jump3;
  }

  function activeItem() {
    const scenario = currentScenario();
    const batch = Array.isArray(scenario.batch) ? scenario.batch : [];

    if (batch.length === 0) {
      return scenario;
    }

    const index = Math.min(Math.max(0, Number(state.batchIndex) || 0), batch.length - 1);
    return batch[index];
  }

  function applyState() {
    alertWindow.dataset.view = state.view;
    alertWindow.dataset.size = state.size;
    alertWindow.dataset.topmost = String(state.topmost);
    alertWindow.style.opacity = String(state.opacity / 100);

    alertWindow.classList.toggle("hide-region", !state.content.region);
    alertWindow.classList.toggle("hide-security", !state.content.security);
    alertWindow.classList.toggle("hide-channel", !state.content.channel);
    alertWindow.classList.toggle("hide-character", !state.content.character);
    alertWindow.classList.toggle("hide-sound", !state.content.sound);

    $("#topmostChip").textContent = state.topmost ? "置顶" : "非置顶";
    $("#pinBtn").classList.toggle("is-active", state.topmost);
    $("#pinBtn").title = state.topmost ? "取消置顶" : "恢复置顶";
    $("#muteBtn").classList.toggle("is-active", state.muted);
    $("#muteBtn").title = state.muted ? "取消静音" : "静音";

    $$("[data-segment]").forEach((group) => {
      const key = group.dataset.segment;
      $$("button", group).forEach((button) => {
        button.classList.toggle("active", button.dataset.value === String(state[key]));
      });
    });

    $$("[data-toggle]").forEach((button) => {
      const key = button.dataset.toggle;
      button.setAttribute("aria-checked", String(Boolean(state[key])));
    });

    $("#opacityRange").value = String(state.opacity);
    $("#opacityValue").textContent = `${state.opacity}%`;
    $("#autoCloseSelect").value = String(state.autoClose);
    $("#positionSelect").value = state.position;
    $("#monitorSelect").value = state.monitor;

    $$("[data-content-toggle]").forEach((input) => {
      input.checked = Boolean(state.content[input.dataset.contentToggle]);
    });

    if (!state.enabled) {
      window.clearInterval(tickId);
      tickId = null;
      alertWindow.classList.add("is-hidden");
      showStageEmpty("弹窗已停用", "打开开关后，命中规则会再次显示");
    } else if (!alertWindow.classList.contains("is-hidden")) {
      stageEmpty.hidden = true;
    }
  }

  function renderAlert() {
    const scenario = currentScenario();
    const batch = Array.isArray(scenario.batch) ? scenario.batch : [];
    const item = activeItem();
    const alert = SYSTEMS[item.route[item.route.length - 1]];
    const regions = [...new Set(item.route.map((name) => SYSTEMS[name].region))];
    const alternatives = Array.isArray(item.alternatives) ? item.alternatives : [];

    $("#alertKickerText").textContent = batch.length > 0
      ? `INTEL ALERT · ${batch.length} 个账号`
      : "INTEL ALERT";
    $("#alertSystem").textContent = alert.name;
    $("#jumpBadge").textContent = item.jumps === 0 ? "同星系" : `${item.jumps} 跳`;
    $("#alertCharacter").textContent = item.character;
    $("#alertChannel").textContent = item.channel;
    $("#alertTime").textContent = item.time;

    $("#ruleText").textContent = item.rule;
    $("#soundText").textContent = `${item.sound} · ${item.volume}%`;
    $("#regionText").textContent = regions.join(" → ");
    $("#securityText").textContent = alert.security.toFixed(2);

    const altBtn = $("#altRouteBtn");
    const altWrap = $("#altRouteViz");
    altBtn.hidden = alternatives.length === 0;
    if (alternatives.length > 0) {
      altBtn.textContent = state.showAlternatives
        ? "收起备选路线"
        : `另有 ${alternatives.length} 条备选路线 · ${alternatives[0].jumps} 跳`;
    }

    // 路线视图用第二行展示备选路线；邻域视图直接以虚线边叠加，避免窗口过高
    if (alternatives.length > 0 && state.showAlternatives && state.view === "route") {
      altWrap.hidden = false;
      altWrap.innerHTML = renderAlternative(alternatives[0]);
    } else {
      altWrap.hidden = true;
      altWrap.innerHTML = "";
    }

    $("#vizTitle").textContent = state.view === "route" ? "最短星门路径" : "5 跳邻域关联图";
    $("#vizHint").textContent = state.view === "route" ? "每段 = 1 跳" : "高亮节点 = 最短路径";

    routeViz.innerHTML = renderRoute(item);
    neighborhoodViz.innerHTML = renderNeighborhood(item);
    routeViz.hidden = state.view !== "route";
    neighborhoodViz.hidden = state.view !== "neighborhood";

    renderBatch(batch, scenario);
    $("#countdown").hidden = false;
    updateCountdownLabel();
  }

  function renderBatch(batch, scenario) {
    const list = $("#batchList");
    const note = $("#batchNote");

    if (batch.length < 2) {
      list.hidden = true;
      list.innerHTML = "";
      note.hidden = true;
      note.textContent = "";
      return;
    }

    const active = Math.min(Math.max(0, Number(state.batchIndex) || 0), batch.length - 1);
    list.hidden = false;
    list.innerHTML = batch.map((item, index) => {
      const from = item.route[0];
      const to = item.route[item.route.length - 1];
      return `<button type="button" class="batch-row${index === active ? " active" : ""}" data-batch-index="${index}">
        <span class="batch-rank">${index + 1}</span>
        <span class="batch-account">${item.character}</span>
        <span class="batch-route">${from} → ${to}</span>
        <span class="batch-jumps">${item.jumps} 跳</span>
      </button>`;
    }).join("");

    note.hidden = false;
    note.textContent = `本批声音：${scenario.batchSound} · 按最紧急规则（${batch[0].rule}）播放`;
  }

  function renderAlternative(alt) {
    const route = alt.route;
    const W = 680;
    const H = 124;
    const nodeW = 86;
    const nodeH = 44;
    const top = 48;
    const gap = (W - route.length * nodeW) / (route.length + 1);
    const parts = [`<defs>
      <marker id="altArrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" markerUnits="userSpaceOnUse" orient="auto">
        <path d="M1 1 L7 4 L1 7 Z" fill="#8A6A32" />
      </marker>
    </defs>
    <text x="8" y="18" font-family="Microsoft YaHei UI, sans-serif" font-size="9" fill="#8A98A6">备选路线 · ${alt.jumps} 跳</text>`];

    for (let i = 0; i < route.length - 1; i++) {
      const x1 = gap + i * (nodeW + gap) + nodeW;
      const x2 = gap + (i + 1) * (nodeW + gap);
      const y = top + nodeH / 2;
      parts.push(`<line x1="${x1}" y1="${y}" x2="${x2 - 4}" y2="${y}" stroke="#8A6A32" stroke-width="1.2" stroke-dasharray="4 3" marker-end="url(#altArrow)" />`);
    }

    route.forEach((name, index) => {
      const system = SYSTEMS[name];
      const x = gap + index * (nodeW + gap);
      parts.push(`<g>
        <rect x="${x}" y="${top}" width="${nodeW}" height="${nodeH}" rx="7" fill="#12100C" stroke="#4A3A22" stroke-width="1" />
        <text x="${x + nodeW / 2}" y="${top + 18}" text-anchor="middle" font-family="Cascadia Mono, Consolas, monospace" font-size="10.5" fill="#B9A27A">${system.name}</text>
        <text x="${x + nodeW / 2}" y="${top + 32}" text-anchor="middle" font-family="Cascadia Mono, Consolas, monospace" font-size="8" fill="#7A6A4A">${system.security.toFixed(2)}</text>
      </g>`);
    });

    return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${route[0]} 到 ${route[route.length - 1]} 的备选星门路线">${parts.join("")}</svg>`;
  }

  function renderRoute(scenario) {
    const route = scenario.route;

    if (route.length === 1) {
      const system = SYSTEMS[route[0]];
      return `<svg viewBox="0 0 680 168" role="img" aria-label="${system.name}，当前位置与警报位置相同">
        <circle cx="340" cy="82" r="62" fill="none" stroke="#FF4D4D" stroke-opacity="0.14" />
        <circle cx="340" cy="82" r="46" fill="none" stroke="#FF4D4D" stroke-opacity="0.28" />
        <rect x="266" y="53" width="148" height="58" rx="9" fill="#1E1114" stroke="#FF4D4D" stroke-width="1.6" />
        <text x="340" y="77" text-anchor="middle" font-family="Cascadia Mono, Consolas, monospace" font-size="13" font-weight="600" fill="#E3EBF2">${system.name}</text>
        <text x="340" y="94" text-anchor="middle" class="sec-line" font-family="Cascadia Mono, Consolas, monospace" font-size="9.5" fill="#FF9090">${system.security.toFixed(2)}</text>
        <text x="340" y="139" text-anchor="middle" font-family="Microsoft YaHei UI, sans-serif" font-size="10" fill="#FF9090">当前位置 = 警报位置</text>
      </svg>`;
    }

    const W = 680;
    const H = 168;
    const nodeW = 96;
    const nodeH = 58;
    const top = 56;
    const gap = (W - route.length * nodeW) / (route.length + 1);
    const parts = [];

    parts.push(`<defs>
      <marker id="routeArrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" markerUnits="userSpaceOnUse" orient="auto">
        <path d="M1 1 L7 4 L1 7 Z" fill="#5A6672" />
      </marker>
    </defs>`);

    for (let i = 0; i < route.length - 1; i++) {
      const x1 = gap + i * (nodeW + gap) + nodeW;
      const x2 = gap + (i + 1) * (nodeW + gap);
      const y = top + nodeH / 2;
      parts.push(`<line x1="${x1}" y1="${y}" x2="${x2 - 5}" y2="${y}" stroke="#3A4A5A" stroke-width="1.4" marker-end="url(#routeArrow)" />`);
      parts.push(`<circle cx="${x1 + 2}" cy="${y}" r="2" fill="#5A6672" />`);
    }

    route.forEach((name, index) => {
      const system = SYSTEMS[name];
      const x = gap + index * (nodeW + gap);
      const role = index === 0 ? "current" : index === route.length - 1 ? "alert" : "via";
      const stroke = role === "current" ? "#F0A02A" : role === "alert" ? "#FF4D4D" : "#2D3944";
      const fill = role === "alert" ? "#1E1114" : role === "current" ? "#1A1710" : "#151B21";
      const roleText = role === "current" ? "当前位置" : role === "alert" ? "警报位置" : "途经";
      const roleColor = role === "current" ? "#F0A02A" : role === "alert" ? "#FF4D4D" : "#5A6672";
      const secColor = role === "alert" ? "#FF9090" : role === "current" ? "#D9AE6C" : "#8A98A6";

      if (role === "alert") {
        parts.push(`<circle class="alert-pulse" cx="${x + nodeW / 2}" cy="${top + nodeH / 2}" r="${nodeW / 2 + 10}" fill="none" stroke="#FF4D4D" stroke-opacity="0.28" />`);
      }

      parts.push(`<g>
        <text x="${x + nodeW / 2}" y="${top - 12}" text-anchor="middle" font-family="Microsoft YaHei UI, sans-serif" font-size="9.5" fill="${roleColor}">${roleText}</text>
        <rect x="${x}" y="${top}" width="${nodeW}" height="${nodeH}" rx="8" fill="${fill}" stroke="${stroke}" stroke-width="1.4" />
        <text x="${x + nodeW / 2}" y="${top + 24}" text-anchor="middle" font-family="Cascadia Mono, Consolas, monospace" font-size="12" font-weight="600" fill="#E3EBF2">${system.name}</text>
        <text x="${x + nodeW / 2}" y="${top + 42}" text-anchor="middle" class="sec-line" font-family="Cascadia Mono, Consolas, monospace" font-size="9.5" fill="${secColor}">${system.security.toFixed(2)}</text>
      </g>`);
    });

    return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="从 ${SYSTEMS[route[0]].name} 到 ${SYSTEMS[route[route.length - 1]].name} 的星门路径">${parts.join("")}</svg>`;
  }

  function renderNeighborhood(scenario) {
    const W = 620;
    const H = 310;
    const nodeW = 84;
    const nodeH = 34;
    const colStep = 98;
    const route = scenario.route;
    const routeSet = new Set(route);
    const routePairs = new Set();
    for (let i = 0; i < route.length - 1; i++) {
      routePairs.add(edgeKey(route[i], route[i + 1]));
    }

    const alternatives = Array.isArray(scenario.alternatives) ? scenario.alternatives : [];
    const altPairs = new Set();
    if (state.showAlternatives) {
      alternatives.forEach((alt) => {
        for (let i = 0; i < alt.route.length - 1; i++) {
          altPairs.add(edgeKey(alt.route[i], alt.route[i + 1]));
        }
      });
    }

    const positions = new Map();
    NEIGHBORHOOD.columns.forEach((column) => {
      const count = column.nodes.length;
      const total = count * nodeH + (count - 1) * 18;
      const start = (H - total) / 2;
      const x = 30 + column.depth * colStep;
      column.nodes.forEach((name, index) => {
        positions.set(name, { x, y: start + index * (nodeH + 18) });
      });
    });

    const parts = [];

    NEIGHBORHOOD.columns.forEach((column) => {
      parts.push(`<text x="${30 + column.depth * colStep + nodeW / 2}" y="20" text-anchor="middle" font-family="Microsoft YaHei UI, sans-serif" font-size="8.5" fill="#5A6672">${column.depth} 跳</text>`);
    });

    NEIGHBORHOOD.edges.forEach(([a, b]) => {
      const pa = positions.get(a);
      const pb = positions.get(b);
      if (!pa || !pb) return;
      const key = edgeKey(a, b);
      const isRoute = routePairs.has(key);
      const isAlt = altPairs.has(key) && !isRoute;
      const stroke = isRoute ? "#F0A02A" : isAlt ? "#8A6A32" : "#2D3944";
      const opacity = isRoute ? "0.9" : isAlt ? "0.8" : "0.55";
      const width = isRoute ? "1.6" : isAlt ? "1.3" : "1";
      const dash = isAlt ? ' stroke-dasharray="4 3"' : "";

      if (pa.x === pb.x) {
        const x = pa.x + nodeW;
        const y1 = pa.y + nodeH / 2;
        const y2 = pb.y + nodeH / 2;
        parts.push(`<path d="M${x},${y1} h7 V${y2} h-7" fill="none" stroke="${stroke}" stroke-opacity="${opacity}" stroke-width="${width}"${dash} />`);
      } else {
        const left = pa.x < pb.x ? pa : pb;
        const right = pa.x < pb.x ? pb : pa;
        parts.push(`<line x1="${left.x + nodeW}" y1="${left.y + nodeH / 2}" x2="${right.x}" y2="${right.y + nodeH / 2}" stroke="${stroke}" stroke-opacity="${opacity}" stroke-width="${width}"${dash} />`);
      }
    });

    const sameSystem = route.length === 1;

    NEIGHBORHOOD.columns.forEach((column) => {
      column.nodes.forEach((name) => {
        const system = SYSTEMS[name];
        const pos = positions.get(name);
        const isAlert = sameSystem ? name === route[0] : name === route[route.length - 1];
        const isCurrent = name === route[0] && !sameSystem;
        const isVia = routeSet.has(name) && !isCurrent && !isAlert;
        const stroke = isCurrent ? "#F0A02A" : isAlert ? "#FF4D4D" : isVia ? "#8A6A32" : "#2D3944";
        const fill = isAlert ? "#1E1114" : isCurrent ? "#1A1710" : "#151B21";
        const textColor = isCurrent || isAlert || isVia ? "#E3EBF2" : "#5A6672";
        const secColor = isAlert ? "#FF9090" : isCurrent ? "#D9AE6C" : isVia ? "#8A98A6" : "#3F4A55";

        parts.push(`<g>
          <rect x="${pos.x}" y="${pos.y}" width="${nodeW}" height="${nodeH}" rx="6" fill="${fill}" stroke="${stroke}" stroke-width="1.2" />
          <text x="${pos.x + nodeW / 2}" y="${pos.y + 14}" text-anchor="middle" font-family="Cascadia Mono, Consolas, monospace" font-size="9.5" font-weight="600" fill="${textColor}">${system.name}</text>
          <text x="${pos.x + nodeW / 2}" y="${pos.y + 26}" text-anchor="middle" class="sec-line" font-family="Cascadia Mono, Consolas, monospace" font-size="8" fill="${secColor}">${system.security.toFixed(2)}</text>
        </g>`);
      });
    });

    return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="2O-EEW 五跳邻域与最短路径">${parts.join("")}</svg>`;
  }

  function edgeKey(a, b) {
    return a < b ? `${a}|${b}` : `${b}|${a}`;
  }

  function startCountdown() {
    window.clearInterval(tickId);
    tickId = null;
    paused = false;

    const total = Number(state.autoClose);
    if (!state.enabled || total <= 0) {
      progressBar.style.width = "0%";
      countdownText.textContent = total <= 0 ? "不会自动关闭" : "弹窗已停用";
      return;
    }

    remaining = total;
    progressBar.style.width = "100%";
    updateCountdownLabel();

    tickId = window.setInterval(() => {
      if (paused || drag) return;
      remaining = Math.max(0, remaining - 0.1);
      progressBar.style.width = `${(remaining / total) * 100}%`;
      updateCountdownLabel();

      if (remaining <= 0) {
        window.clearInterval(tickId);
        tickId = null;
        hideAlert("已自动关闭", "点击「模拟报警」重新显示");
      }
    }, 100);
  }

  function updateCountdownLabel() {
    if (!state.enabled) {
      countdownText.textContent = "弹窗已停用";
      return;
    }

    if (Number(state.autoClose) <= 0) {
      countdownText.textContent = "不会自动关闭";
      return;
    }

    countdownText.textContent = `${Math.ceil(remaining)} 秒后自动关闭`;
  }

  function showAlert(forceEnable = false) {
    if (forceEnable) {
      state.enabled = true;
    }

    if (!state.enabled) {
      applyState();
      saveState();
      return;
    }

    alertWindow.classList.remove("is-hidden");
    alertWindow.style.animation = "none";
    void alertWindow.offsetWidth;
    alertWindow.style.animation = "";
    stageEmpty.hidden = true;
    applyPosition();
    renderAlert();
    startCountdown();
    applyState();
    saveState();
  }

  function hideAlert(title, detail) {
    window.clearInterval(tickId);
    tickId = null;
    alertWindow.classList.add("is-hidden");
    showStageEmpty(title, detail);
  }

  function showStageEmpty(title, detail) {
    stageEmpty.hidden = false;
    $("#stageEmptyTitle").textContent = title;
    $("#stageEmptyDetail").textContent = detail;
  }

  function showToast(message) {
    toast.textContent = message;
    toast.classList.add("show");
    window.clearTimeout(toastId);
    toastId = window.setTimeout(() => toast.classList.remove("show"), 1800);
  }

  function applyPosition() {
    if (state.position === "custom" && Number.isFinite(state.customLeft) && Number.isFinite(state.customTop)) {
      const maxLeft = Math.max(8, stage.clientWidth - alertWindow.offsetWidth - 8);
      const maxTop = Math.max(8, stage.clientHeight - alertWindow.offsetHeight - 8);

      alertWindow.dataset.position = "custom";
      alertWindow.style.left = `${Math.min(Math.max(8, state.customLeft), maxLeft)}px`;
      alertWindow.style.top = `${Math.min(Math.max(8, state.customTop), maxTop)}px`;
      alertWindow.style.right = "auto";
      alertWindow.style.bottom = "auto";
      alertWindow.style.translate = "none";
      return;
    }

    alertWindow.dataset.position = state.position === "custom" ? "top-right" : state.position;
    alertWindow.style.left = "";
    alertWindow.style.top = "";
    alertWindow.style.right = "";
    alertWindow.style.bottom = "";
    alertWindow.style.translate = "";
  }

  function captureCurrentPosition() {
    const stageRect = stage.getBoundingClientRect();
    const rect = alertWindow.getBoundingClientRect();
    state.customLeft = Math.round(rect.left - stageRect.left);
    state.customTop = Math.round(rect.top - stageRect.top);
  }

  function resetPosition() {
    state.position = "top-right";
    state.customLeft = null;
    state.customTop = null;
    applyPosition();
  }

  function bindEvents() {
    $$("[data-segment]").forEach((group) => {
      group.addEventListener("click", (event) => {
        const button = event.target.closest("button[data-value]");
        if (!button) return;
        const key = group.dataset.segment;
        state[key] = button.dataset.value;

        if (key === "scenario") {
          state.batchIndex = 0;
          state.showAlternatives = false;
          showAlert();
          return;
        }

        applyState();
        renderAlert();
        saveState();
      });
    });

    $$("[data-toggle]").forEach((button) => {
      button.addEventListener("click", () => {
        const key = button.dataset.toggle;
        state[key] = !state[key];

        if (key === "enabled" && state.enabled) {
          showAlert();
          return;
        }

        applyState();
        saveState();
      });
    });

    $("#opacityRange").addEventListener("input", (event) => {
      state.opacity = Number(event.target.value);
      $("#opacityValue").textContent = `${state.opacity}%`;
      alertWindow.style.opacity = String(state.opacity / 100);
      saveState();
    });

    $("#autoCloseSelect").addEventListener("change", (event) => {
      state.autoClose = Number(event.target.value);
      saveState();
      startCountdown();
    });

    $("#positionSelect").addEventListener("change", (event) => {
      const value = event.target.value;

      if (value === "custom") {
        captureCurrentPosition();
        state.position = "custom";
        applyPosition();
      } else {
        state.position = value;
        state.customLeft = null;
        state.customTop = null;
        applyPosition();
      }

      applyState();
      saveState();
    });

    $("#monitorSelect").addEventListener("change", (event) => {
      state.monitor = event.target.value;
      saveState();
      showToast(event.target.selectedOptions[0]?.textContent ?? "显示器已切换");
    });

    $$("[data-content-toggle]").forEach((input) => {
      input.addEventListener("change", () => {
        state.content[input.dataset.contentToggle] = input.checked;
        applyState();
        saveState();
      });
    });

    $("#batchList").addEventListener("click", (event) => {
      const row = event.target.closest("[data-batch-index]");
      if (!row) return;

      state.batchIndex = Number(row.dataset.batchIndex) || 0;
      state.showAlternatives = false;
      renderAlert();
      saveState();
    });

    $("#altRouteBtn").addEventListener("click", () => {
      state.showAlternatives = !state.showAlternatives;
      renderAlert();
      saveState();
    });

    $("#simulateBtn").addEventListener("click", () => showAlert(true));
    $("#closeBtn").addEventListener("click", () => hideAlert("弹窗已关闭", "点击「模拟报警」重新显示"));

    $("#pinBtn").addEventListener("click", () => {
      state.topmost = !state.topmost;
      applyState();
      saveState();
      showToast(state.topmost ? "已恢复置顶" : "已取消置顶，可能被游戏窗口遮挡");
    });

    $("#muteBtn").addEventListener("click", () => {
      state.muted = !state.muted;
      applyState();
      saveState();
      showToast(state.muted ? "预警音已静音" : "预警音已恢复");
    });

    $("#copyBtn").addEventListener("click", async () => {
      const system = activeItem().route.at(-1);
      try {
        await navigator.clipboard.writeText(system);
        showToast(`已复制星系 ${system}`);
      } catch {
        showToast(`星系：${system}`);
      }
    });

    $("#focusBtn").addEventListener("click", () => {
      showToast(`已请求切换到 ${activeItem().character}（原型模拟）`);
    });

    $("#resetBtn").addEventListener("click", () => {
      try {
        localStorage.removeItem(STORAGE_KEY);
      } catch {
        /* 忽略 */
      }
      Object.assign(state, structuredClone(DEFAULTS));
      resetPosition();
      showAlert(true);
      showToast("设置已重置");
    });

    alertWindow.addEventListener("pointerenter", () => {
      paused = true;
    });
    alertWindow.addEventListener("pointerleave", () => {
      paused = false;
    });

    bindDrag();
  }

  function bindDrag() {
    alertHead.addEventListener("pointerdown", (event) => {
      if (event.target.closest("button")) return;
      if (event.button !== 0) return;

      const stageRect = stage.getBoundingClientRect();
      const rect = alertWindow.getBoundingClientRect();

      alertWindow.dataset.position = "custom";
      alertWindow.style.translate = "none";
      alertWindow.style.left = `${rect.left - stageRect.left}px`;
      alertWindow.style.top = `${rect.top - stageRect.top}px`;
      alertWindow.style.right = "auto";
      alertWindow.style.bottom = "auto";

      drag = {
        pointerId: event.pointerId,
        startX: event.clientX,
        startY: event.clientY,
        left: rect.left - stageRect.left,
        top: rect.top - stageRect.top,
        moved: false,
      };

      alertHead.setPointerCapture(event.pointerId);
      alertHead.classList.add("dragging");
      event.preventDefault();
    });

    alertHead.addEventListener("pointermove", (event) => {
      if (!drag || event.pointerId !== drag.pointerId) return;

      if (!drag.moved) {
        if (Math.abs(event.clientX - drag.startX) < 3 && Math.abs(event.clientY - drag.startY) < 3) {
          return;
        }

        drag.moved = true;
      }

      const stageRect = stage.getBoundingClientRect();
      const maxLeft = stageRect.width - alertWindow.offsetWidth - 8;
      const maxTop = stageRect.height - alertWindow.offsetHeight - 8;
      const left = Math.min(Math.max(8, drag.left + event.clientX - drag.startX), Math.max(8, maxLeft));
      const top = Math.min(Math.max(8, drag.top + event.clientY - drag.startY), Math.max(8, maxTop));

      alertWindow.style.left = `${left}px`;
      alertWindow.style.top = `${top}px`;
    });

    const endDrag = (event) => {
      if (!drag || event.pointerId !== drag.pointerId) return;

      const moved = drag.moved;
      drag = null;
      alertHead.classList.remove("dragging");
      paused = alertWindow.matches(":hover");

      if (moved) {
        state.position = "custom";
        state.customLeft = Number.parseFloat(alertWindow.style.left) || 0;
        state.customTop = Number.parseFloat(alertWindow.style.top) || 0;
        $("#positionSelect").value = "custom";
        saveState();
        showToast("自定义位置已保存");
      } else {
        applyPosition();
      }
    };

    alertHead.addEventListener("pointerup", endDrag);
    alertHead.addEventListener("pointercancel", endDrag);
  }

  bindEvents();
  bindRuleEvents();
  bindListenerEvents();
  renderRules();
  renderListeners();
  renderProbe();
  applyPosition();
  applyState();
  renderAlert();
  startCountdown();
})();
