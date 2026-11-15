(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));
  const webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const params = new URLSearchParams(window.location.search);
  const isCapture = params.has('capture');
  const icons = {
    overview: 'overview', devices: 'chip', updates: 'update', settings: 'settings',
    scan: 'scan', shield: 'shield', download: 'download', check: 'check', alert: 'alert',
    refresh: 'refresh', folder: 'folder', external: 'external', info: 'info', clock: 'clock',
    cpu: 'cpu', chip: 'chip', wrench: 'wrench', database: 'database', bolt: 'bolt', lock: 'lock',
    close: 'close', chevron: 'chevron', user: 'user',
  };

  const state = {
    settings: { theme: 'dark', autoScan: true, oldDriverYears: 5 },
    system: null,
    isAdmin: false,
    scan: null,
    updates: [],
    updatesOk: true,
    updatesMessage: '',
    updatesLoading: false,
    selectedUpdates: new Set(),
    scanning: false,
    installing: false,
    importing: false,
    activePage: 'overview',
    deviceFilter: 'all',
    deviceSearch: '',
    themeMode: 'dark',
    modalResolver: null,
    scanProgress: null,
    updateProgress: null,
    installProgress: null,
    toastSeq: 0,
  };

  const pending = new Map();
  let requestSeq = 0;

  document.addEventListener('DOMContentLoaded', init);

  function init() {
    if (isCapture) document.documentElement.classList.add('is-capture');
    const queryTheme = params.get('theme');
    if (queryTheme === 'light' || queryTheme === 'dark') {
      state.themeMode = queryTheme;
      applyTheme(queryTheme, false);
    } else {
      applyTheme('dark', false);
    }
    bindEvents();
    initSystemTheme();
    if (webview) webview.addEventListener('message', onMessage);
    loadInitial();
  }

  function icon(name, cls = '') {
    return `<svg class="icon ${cls}"><use href="#i-${icons[name] || name}"></use></svg>`;
  }

  /* ------------------------------------------------------------------ */
  /* bridge                                                             */
  /* ------------------------------------------------------------------ */
  function call(action, payload = {}, timeoutMs = 600000) {
    if (!webview) return Promise.reject(new Error('未检测到 WebView2 桥接。'));
    return new Promise((resolve, reject) => {
      const id = ++requestSeq;
      const timer = window.setTimeout(() => {
        pending.delete(id);
        reject(new Error('操作超时，请稍后重试。'));
      }, timeoutMs);
      pending.set(id, { resolve, reject, timer });
      try {
        webview.postMessage({ id, action, payload });
      } catch (error) {
        pending.delete(id);
        window.clearTimeout(timer);
        reject(error);
      }
    });
  }

  function onMessage(event) {
    let message = event.data;
    if (typeof message === 'string') {
      try { message = JSON.parse(message); } catch { return; }
    }
    if (!message || typeof message !== 'object') return;

    if (message.type === 'response') {
      const waiter = pending.get(message.id);
      if (!waiter) return;
      pending.delete(message.id);
      window.clearTimeout(waiter.timer);
      if (message.ok) waiter.resolve(message.data);
      else waiter.reject(new Error(message.error || '操作失败。'));
      return;
    }

    if (message.type === 'event') {
      if (message.event === 'scanProgress') applyScanProgress(message.data || {});
      else if (message.event === 'updateProgress') applyUpdateProgress(message.data || {});
      else if (message.event === 'installProgress') applyInstallProgress(message.data || {});
      else if (message.event === 'folderInstallProgress') applyInstallProgress(message.data || {});
    }
  }

  async function loadInitial() {
    try {
      const data = await call('getInitial', {}, 30000);
      state.system = data.system || null;
      state.settings = data.settings || state.settings;
      state.isAdmin = Boolean(data.isAdmin);
      const queryTheme = params.get('theme');
      state.themeMode = (queryTheme === 'light' || queryTheme === 'dark')
        ? queryTheme
        : (state.settings.theme || 'dark');
      applyTheme(state.themeMode, false);
      renderSystem();
      renderAdmin();
      renderSettings();
      applyThemeOptions();
      updateTopStatus();
      const initialTab = params.get('tab');
      if (initialTab) setTab(initialTab);
      else setTab('overview');
      renderAll();

      const auto = state.settings.autoScan !== false;
      if (auto || (isCapture && params.get('autoscan') === '1')) {
        window.setTimeout(() => scanDrivers(false), 280);
      }
    } catch (error) {
      toast('无法读取驱动信息', error.message, 'error', 7000);
      updateTopStatus('初始化失败');
    }
  }

  /* ------------------------------------------------------------------ */
  /* scan / updates                                                     */
  /* ------------------------------------------------------------------ */
  async function scanDrivers(manual = true) {
    if (state.scanning || state.installing) return;
    state.scanning = true;
    state.scanProgress = { percent: 0, message: '正在准备扫描…' };
    state.selectedUpdates.clear();
    showScanProgress(true);
    setBusyButtons();
    updateTopStatus('正在扫描设备与驱动');
    renderOverview();
    try {
      const result = await call('scanDrivers', {}, 600000);
      state.scan = result;
      state.scanProgress = { percent: 100, message: '设备扫描完成' };
      renderAll();
      if (manual) toast('驱动检查完成', `已识别 ${result.totalDevices} 个设备，${result.missingCount} 个缺失驱动。`, 'success', 5000);
      showScanProgress(false, 650);
      // Query Windows Update after hardware scan.
      window.setTimeout(() => searchUpdates(false), 220);
    } catch (error) {
      state.scanProgress = null;
      showScanProgress(false);
      toast('驱动检查失败', error.message, 'error', 7000);
      updateTopStatus('扫描失败');
    } finally {
      state.scanning = false;
      setBusyButtons();
      renderOverview();
      renderSystem();
    }
  }

  async function searchUpdates(manual = true) {
    if (state.updatesLoading || state.installing) return;
    state.updatesLoading = true;
    state.updatesMessage = '';
    state.updateProgress = { percent: 4, message: '正在连接 Windows Update…' };
    showUpdateProgress(true);
    renderUpdates();
    renderOverview();
    updateTopStatus('正在查询 Windows Update 驱动');
    try {
      const result = await call('searchDriverUpdates', {}, 600000);
      state.updates = result.updates || [];
      state.updatesOk = Boolean(result.ok);
      state.updatesMessage = result.message || '';
      state.selectedUpdates = new Set(state.updates.map(item => item.id));
      state.updateProgress = { percent: 100, message: result.message || '查询完成' };
      renderAll();
      if (manual) {
        toast(
          result.ok ? (state.updates.length ? '发现驱动更新' : '驱动已是最新') : 'Windows Update 查询未完成',
          result.ok
            ? (state.updates.length ? `找到 ${state.updates.length} 个驱动更新。` : 'Windows Update 当前没有可用驱动更新。')
            : result.message,
          result.ok ? (state.updates.length ? 'success' : 'info') : 'warning',
          6000
        );
      }
    } catch (error) {
      state.updatesOk = false;
      state.updatesMessage = error.message;
      state.updates = [];
      state.selectedUpdates.clear();
      toast('驱动更新查询失败', error.message, 'error', 7000);
    } finally {
      state.updatesLoading = false;
      state.updateProgress = null;
      showUpdateProgress(false, 650);
      renderAll();
      updateTopStatus();
    }
  }

  async function installSelected() {
    if (state.installing) return;
    const ids = Array.from(state.selectedUpdates);
    if (!ids.length) {
      toast('没有选择更新', '请勾选要安装的驱动更新。', 'warning');
      return;
    }
    if (!state.isAdmin) {
      toast('需要管理员权限', '安装驱动需要在 UAC 中授予管理员权限后重新打开驱界。', 'error', 7000);
      return;
    }
    const ok = await confirmDialog({
      title: `安装 ${ids.length} 个驱动更新？`,
      sub: 'Windows Update · 驱动类别',
      body: '安装过程中屏幕可能闪烁，部分驱动需要重启电脑后生效。安装期间请不要关闭电脑或强制断电。',
      okText: '下载并安装',
      icon: 'download',
    });
    if (!ok) return;

    state.installing = true;
    state.installProgress = { percent: 2, stage: 'prepare', message: '正在准备驱动安装…' };
    showUpdateProgress(true);
    setBusyButtons();
    renderUpdates();
    try {
      const result = await call('installDriverUpdates', { ids }, 1800000);
      state.installProgress = {
        percent: 100,
        stage: result.ok ? 'done' : 'error',
        message: result.message || (result.ok ? '驱动安装完成' : '驱动安装未完成'),
      };
      renderUpdates();
      if (result.ok) {
        toast('驱动安装已提交', result.message + (result.rebootRequired ? ' 请重启电脑。' : ''), 'success', 9000);
      } else {
        toast('驱动安装未完成', result.message || 'Windows Update 没有完成安装。', 'error', 9000);
      }
      window.setTimeout(() => {
        showUpdateProgress(false, 900);
        scanDrivers(false);
      }, 1500);
    } catch (error) {
      toast('驱动安装失败', error.message, 'error', 9000);
      showUpdateProgress(false);
    } finally {
      state.installing = false;
      setBusyButtons();
      renderUpdates();
      renderOverview();
    }
  }

  async function importDriverFolder() {
    if (state.importing) return;
    try {
      const picked = await call('pickDriverFolder');
      const folder = picked && picked.path ? picked.path : '';
      if (!folder) return;
      const ok = await confirmDialog({
        title: '从文件夹安装驱动？',
        sub: folder,
        body: '驱界将调用 Windows 自带的 pnputil 安装文件夹中的 .inf 驱动包。请确认驱动来源可信。',
        okText: '开始安装',
        icon: 'folder',
      });
      if (!ok) return;
      state.importing = true;
      showUpdateProgress(true);
      state.installProgress = { percent: 5, stage: 'install', message: '正在调用 pnputil 安装驱动…' };
      renderUpdates();
      const result = await call('installDriverFolder', { folder }, 900000);
      toast(result.ok ? '驱动包已提交安装' : '驱动安装未完成', result.message, result.ok ? 'success' : 'error', 9000);
      showUpdateProgress(false, 900);
      window.setTimeout(() => scanDrivers(false), 800);
    } catch (error) {
      toast('无法导入驱动', error.message, 'error', 7000);
    } finally {
      state.importing = false;
      renderUpdates();
    }
  }

  /* ------------------------------------------------------------------ */
  /* render: summary                                                    */
  /* ------------------------------------------------------------------ */
  function renderAll() {
    renderOverview();
    renderDevices();
    renderUpdates();
    renderSettings();
    renderAdmin();
    updateTopStatus();
  }

  function renderOverview() {
    const scan = state.scan;
    const missing = scan ? scan.missingCount : 0;
    const problem = scan ? scan.problemCount : 0;
    const old = scan ? scan.oldCount : 0;
    const unknown = scan ? scan.unknownCount : 0;
    const total = scan ? scan.totalDevices : 0;
    const score = scan ? scan.healthScore : 0;
    const updates = state.updates.length;

    setText('#statMissing', scan ? missing : '--');
    setText('#statMissingSub', scan ? (problem ? `${problem} 个设备异常` : (missing ? '需要安装驱动' : '没有发现缺失驱动')) : '未扫描');
    setText('#statOld', scan ? old : '--');
    setText('#statOldSub', scan ? (old ? '超过设定年限' : '驱动日期较新') : '未扫描');
    setText('#statUpdates', state.updatesLoading ? '…' : (state.updatesOk ? updates : '--'));
    setText('#statUpdatesSub', state.updatesLoading ? '正在查询 Windows Update' : (state.updatesOk ? (updates ? '来自 Windows Update' : '当前没有可用更新') : '查询失败'));
    setText('#statDevices', scan ? total : '--');
    setText('#statDevicesSub', scan ? (unknown ? `${unknown} 个版本未知` : '本机在线设备') : '未扫描');
    setBar('#statMissingBar', scan ? Math.min(100, missing * 22 + problem * 8) : 0);
    setBar('#statOldBar', scan ? Math.min(100, old * 8) : 0);
    setBar('#statUpdatesBar', state.updatesOk ? Math.min(100, updates * 18) : 0);
    setBar('#statDevicesBar', scan ? Math.min(100, total / 2) : 0);

    setText('#heroMissing', scan ? missing : '--');
    setText('#heroOld', scan ? old : '--');
    setText('#heroUpdates', state.updatesLoading ? '…' : (state.updatesOk ? updates : '--'));
    setText('#sideMissing', scan ? missing : '--');
    setText('#sideOld', scan ? old : '--');
    setText('#sideUpdates', state.updatesLoading ? '…' : (state.updatesOk ? updates : '--'));
    setText('#healthScore', scan ? score : '--');
    setText('#sideHealthScore', scan ? score : '--');
    setBar('#sideHealthBar', scan ? score : 0);
    setText('#sidebarHealthLabel', scan ? (score >= 85 ? '良好' : score >= 65 ? '一般' : '需维护') : '待检测');
    setText('#healthCaption', scan ? scan.healthText : '等待扫描');
    setText('#sidebarUpdated', scan ? `扫描于 ${formatClock(new Date(scan.scannedAt))}` : '等待首次扫描');

    const ring = $('#healthRing');
    if (ring) {
      const p = scan ? score : 0;
      const color = score >= 78 ? 'var(--mint)' : score >= 55 ? 'var(--amber)' : 'var(--red)';
      ring.style.background = `conic-gradient(from 210deg, var(--cyan) 0%, var(--blue) 44%, ${color} ${p}%, var(--track-strong) ${p}%, var(--track-strong) 100%)`;
    }

    let kicker = '正在准备驱动检测';
    let title = '先扫描一次，看看系统缺什么。';
    let text = '驱动缺失会让设备出现黄色感叹号、无法联网、没有声音或分辨率异常。驱界会读取真实设备状态，不会伪造扫描结果。';
    if (state.scanning) {
      kicker = '正在扫描本机设备';
      title = '正在读取设备与驱动信息…';
      text = '这通常只需要几秒钟。扫描完成后会自动查询 Windows Update 驱动更新。';
    } else if (scan) {
      if (missing > 0) {
        kicker = '发现需要处理的设备';
        title = `有 ${missing} 个设备缺少驱动。`;
        text = '这些设备可能是新硬件、重装系统后尚未安装驱动，或驱动被误删。安装驱动后会重新扫描验证。';
      } else if (problem > 0) {
        kicker = '驱动状态需要关注';
        title = `有 ${problem} 个设备驱动异常。`;
        text = '可以查看设备详情和问题代码，必要时使用厂商驱动或导入 .inf 驱动包。';
      } else if (updates > 0) {
        kicker = 'Windows Update 有新驱动';
        title = `找到 ${updates} 个驱动更新。`;
        text = '这些更新来自 Windows Update 驱动类别。可以查看详情后一键下载安装，部分驱动需要重启。';
      } else if (old > 0) {
        kicker = '可以进一步优化';
        title = `有 ${old} 个驱动已经较旧。`;
        text = '驱动日期较旧不一定代表有问题；如果设备工作正常，不需要为了“新”而冒险更新。';
      } else {
        kicker = '设备状态良好';
        title = '驱动状态正常，设备都很稳定。';
        text = `已识别 ${total} 个在线设备，当前没有发现缺失或异常驱动。可以定期回来检查。`;
      }
    }
    setText('#heroKicker', kicker);
    setText('#heroTitle', title);
    setText('#heroText', text);
    setText('#heroMissing', scan ? missing : '--');
    setText('#heroOld', scan ? old : '--');
    setText('#heroUpdates', state.updatesLoading ? '…' : (state.updatesOk ? updates : '--'));

    const heroBtn = $('#heroUpdateBtn');
    if (heroBtn) {
      heroBtn.hidden = !updates || state.updatesLoading || !state.updatesOk;
      setText('#heroUpdateLabel', `安装 ${updates} 个驱动更新`);
    }

    renderIssues();
    renderUpdatePreview();
    updateNavBadges();
    setText('#topStatus', state.scanning ? '正在扫描设备与驱动' : state.updatesLoading ? '正在查询 Windows Update 驱动' : (state.isAdmin ? `管理员权限 · ${scan ? total : '--'} 个在线设备` : '标准权限 · 驱动安装不可用'));
    setText('#sidebarVersion', `驱界 v${state.system?.appVersion || '1.0.0'}`);
  }

  function renderIssues() {
    const target = $('#issueList');
    if (!target) return;
    if (!state.scan) {
      target.innerHTML = '<div class="empty-inline">扫描后在这里显示需要处理的设备。</div>';
      return;
    }
    const list = state.scan.devices
      .filter(d => d.isMissing || d.hasProblem || d.isOld || d.status === 'unknown')
      .slice(0, 6);
    if (!list.length) {
      target.innerHTML = '<div class="empty-inline" style="padding:28px 19px;color:var(--mint)">没有发现需要关注的设备。</div>';
      return;
    }
    target.innerHTML = list.map(device => `
      <div class="issue-row" data-device-id="${escapeHtml(device.id)}">
        <span class="issue-dot ${escapeHtml(device.status)}"></span>
        <div class="issue-main">
          <b>${escapeHtml(device.name)}</b>
          <small>${escapeHtml(device.className)} · ${escapeHtml(device.manufacturer || '未知制造商')}${device.problemText ? ' · ' + escapeHtml(device.problemText) : ''}</small>
        </div>
        <span class="status-badge ${escapeHtml(device.status)}">${escapeHtml(device.statusText)}</span>
      </div>`).join('');
    $$('.issue-row', target).forEach(row => row.addEventListener('click', () => openDevice(row.dataset.deviceId)));
  }

  function renderUpdatePreview() {
    const target = $('#updatePreviewList');
    if (!target) return;
    if (state.updatesLoading) {
      target.innerHTML = '<div class="empty-inline">正在连接 Windows Update，查询驱动更新…</div>';
      setText('#updatePreviewSub', '正在查询 Windows Update');
      setText('#installAllBtn', '查询中');
      $('#installAllBtn').disabled = true;
      return;
    }
    if (!state.updatesOk) {
      target.innerHTML = `<div class="empty-inline">${escapeHtml(state.updatesMessage || 'Windows Update 查询失败')}</div>`;
      setText('#updatePreviewSub', '查询未完成');
      $('#installAllBtn').disabled = true;
      return;
    }
    if (!state.updates.length) {
      target.innerHTML = '<div class="empty-inline" style="color:var(--mint)">Windows Update 当前没有可用驱动更新。</div>';
      setText('#updatePreviewSub', '当前没有可用更新');
      $('#installAllBtn').disabled = true;
      return;
    }
    setText('#updatePreviewSub', `找到 ${state.updates.length} 个驱动更新`);
    target.innerHTML = state.updates.slice(0, 3).map(update => `
      <div class="update-preview-row">
        <div class="update-preview-icon">${icon('update')}</div>
        <div class="update-preview-main"><b>${escapeHtml(update.title)}</b><small>${escapeHtml(update.manufacturer || update.provider || 'Microsoft')} · ${escapeHtml(update.versionDate || '日期未知')}</small></div>
        <span class="status-badge ok">可更新</span>
      </div>`).join('');
    const btn = $('#installAllBtn');
    btn.disabled = state.installing;
    btn.innerHTML = `${icon('download')}安装全部`;
  }

  function updateNavBadges() {
    const deviceBadge = $('#navDeviceBadge');
    const updateBadge = $('#navUpdateBadge');
    if (deviceBadge) {
      const count = state.scan ? state.scan.missingCount + state.scan.problemCount : 0;
      deviceBadge.hidden = count === 0;
      deviceBadge.textContent = count;
    }
    if (updateBadge) {
      updateBadge.hidden = !state.updates.length;
      updateBadge.textContent = state.updates.length;
    }
  }

  /* ------------------------------------------------------------------ */
  /* render: devices                                                    */
  /* ------------------------------------------------------------------ */
  function renderDevices() {
    const scan = state.scan;
    const all = scan ? scan.devices : [];
    const counts = {
      all: all.length,
      missing: all.filter(d => d.isMissing).length,
      problem: all.filter(d => d.hasProblem).length,
      old: all.filter(d => d.isOld).length,
      ok: all.filter(d => d.status === 'ok').length,
    };
    setText('#filterAllCount', counts.all);
    setText('#filterMissingCount', counts.missing);
    setText('#filterProblemCount', counts.problem);
    setText('#filterOldCount', counts.old);
    setText('#filterOkCount', counts.ok);

    const target = $('#deviceRows');
    const empty = $('#deviceEmpty');
    if (!target) return;
    const search = state.deviceSearch.trim().toLowerCase();
    let list = all.filter(device => {
      if (state.deviceFilter === 'missing' && !device.isMissing) return false;
      if (state.deviceFilter === 'problem' && !device.hasProblem) return false;
      if (state.deviceFilter === 'old' && !device.isOld) return false;
      if (state.deviceFilter === 'ok' && device.status !== 'ok') return false;
      if (!search) return true;
      return [device.name, device.className, device.manufacturer, device.driverVersion, device.hardwareId]
        .some(value => String(value || '').toLowerCase().includes(search));
    });

    const order = { missing: 0, problem: 1, old: 2, unknown: 3, ok: 4 };
    list = list.slice().sort((a, b) => (order[a.status] ?? 9) - (order[b.status] ?? 9) || String(a.name).localeCompare(String(b.name), 'zh-CN'));
    setText('#deviceCountLabel', `${list.length} 个设备`);
    if (!list.length) {
      target.innerHTML = '';
      empty.hidden = false;
      return;
    }
    empty.hidden = true;
    target.innerHTML = list.map(device => `
      <tr data-device-id="${escapeHtml(device.id)}">
        <td><div class="device-cell"><span class="device-avatar">${icon(deviceIcon(device))}</span><span class="device-name"><b>${escapeHtml(device.name)}</b><small>${escapeHtml(device.problemText || device.instanceId)}</small></span></div></td>
        <td>${escapeHtml(device.className)}</td>
        <td>${escapeHtml(device.manufacturer || '未知')}</td>
        <td class="mono">${escapeHtml(device.driverVersion || '--')}</td>
        <td class="mono">${escapeHtml(device.driverDate || '--')}</td>
        <td><span class="status-badge ${escapeHtml(device.status)}">${escapeHtml(device.statusText)}</span></td>
      </tr>`).join('');
    $$('#deviceRows tr').forEach(row => row.addEventListener('click', () => openDevice(row.dataset.deviceId)));
  }

  function deviceIcon(device) {
    const cls = String(device.classKey || '').toLowerCase();
    if (cls.includes('display') || cls.includes('monitor')) return 'overview';
    if (cls.includes('net') || cls.includes('bluetooth')) return 'database';
    if (cls.includes('media') || cls.includes('audio')) return 'bolt';
    if (cls.includes('usb') || cls.includes('hid') || cls.includes('keyboard') || cls.includes('mouse')) return 'chip';
    return 'chip';
  }

  function openDevice(id) {
    if (!state.scan) return;
    const device = state.scan.devices.find(item => item.id === id);
    if (!device) return;
    $('#drawerRoot').hidden = false;
    $('#drawerName').textContent = device.name;
    $('#drawerSub').textContent = `${device.className} · ${device.manufacturer || '未知制造商'}`;
    $('#drawerAvatar').textContent = initialOf(device.name);
    $('#drawerBody').innerHTML = `
      <div class="detail-hero">
        <div class="process-avatar">${icon(deviceIcon(device))}</div>
        <div><b>${escapeHtml(device.statusText)}</b><small>${escapeHtml(device.problemText || '设备状态正常')}</small></div>
      </div>
      <div class="detail-grid">
        ${detailCell('驱动版本', device.driverVersion || '--')}
        ${detailCell('驱动日期', device.driverDate || '--')}
        ${detailCell('驱动年龄', device.ageYears ? device.ageYears + ' 年' : '--')}
        ${detailCell('问题代码', device.problemCode || '无')}
      </div>
      ${device.problemText ? `<div class="problem-note">${escapeHtml(device.problemText)}。可以在 Windows Update 或厂商官网查找对应驱动。</div>` : ''}
      <div class="detail-section">
        <div class="detail-section-title">设备信息</div>
        ${detailLine('设备类型', device.className)}
        ${detailLine('制造商', device.manufacturer || '--')}
        ${detailLine('驱动提供者', device.driverProvider || '--')}
        ${detailLine('位置', device.location || '--')}
      </div>
      <div class="detail-section">
        <div class="detail-section-title">硬件标识</div>
        ${detailLine('实例 ID', device.instanceId)}
        ${detailLine('硬件 ID', device.hardwareId || '--')}
      </div>`;
    $('#deviceManagerBtn').onclick = openDeviceManager;
  }

  function closeDrawer() {
    $('#drawerRoot').hidden = true;
  }

  /* ------------------------------------------------------------------ */
  /* render: updates                                                    */
  /* ------------------------------------------------------------------ */
  function renderUpdates() {
    const list = $('#updateList');
    const empty = $('#updateEmpty');
    if (!list) return;
    setText('#updateListSub', state.updatesLoading ? '正在查询 Windows Update' : (state.updatesOk ? `${state.updates.length} 个驱动更新` : '查询未完成'));
    if (state.updatesLoading) {
      list.innerHTML = '<div class="empty-inline">正在连接 Windows Update 并查询驱动更新，可能需要几十秒…</div>';
      empty.hidden = true;
    } else if (!state.updatesOk) {
      list.innerHTML = `<div class="empty-inline">${escapeHtml(state.updatesMessage || 'Windows Update 查询失败')}</div>`;
      empty.hidden = true;
    } else if (!state.updates.length) {
      list.innerHTML = '';
      empty.hidden = false;
      setText('#updateEmptyTitle', '暂无可用驱动更新');
      setText('#updateEmptyText', 'Windows Update 当前没有提供驱动更新。缺失驱动可以尝试厂商官方驱动或使用文件夹导入。');
    } else {
      empty.hidden = true;
      list.innerHTML = state.updates.map(update => {
        const selected = state.selectedUpdates.has(update.id);
        return `<div class="update-row">
          <label class="update-check"><input type="checkbox" data-update-id="${escapeHtml(update.id)}" ${selected ? 'checked' : ''}></label>
          <div class="update-glyph">${icon('update')}</div>
          <div class="update-main">
            <div class="update-title">${escapeHtml(update.title)}</div>
            <div class="update-meta">
              <span><b>类型</b> ${escapeHtml(update.driverClass || '驱动')}</span>
              <span><b>厂商</b> ${escapeHtml(update.manufacturer || update.provider || 'Microsoft')}</span>
              ${update.versionDate ? `<span><b>日期</b> ${escapeHtml(update.versionDate)}</span>` : ''}
              ${update.sizeBytes ? `<span><b>大小</b> ${formatBytes(update.sizeBytes)}</span>` : ''}
            </div>
            ${update.description ? `<div class="update-desc">${escapeHtml(update.description)}</div>` : ''}
          </div>
          <div class="update-side">
            <small>${update.isDownloaded ? '已下载' : '待下载'}</small>
            ${update.rebootRequired ? '<span class="reboot-tag">需重启</span>' : ''}
          </div>
        </div>`;
      }).join('');
      $$('#updateList [data-update-id]').forEach(box => {
        box.addEventListener('change', () => {
          if (box.checked) state.selectedUpdates.add(box.dataset.updateId);
          else state.selectedUpdates.delete(box.dataset.updateId);
          updateSelectionUi();
        });
      });
    }
    setText('#updateStatusTitle', state.updatesLoading
      ? '正在查询 Windows Update 驱动'
      : (!state.updatesOk ? 'Windows Update 查询未完成' : (state.updates.length ? '发现可用的驱动更新' : '没有可用驱动更新')));
    setText('#updateStatusText', state.updatesLoading
      ? '只查询驱动类型更新，不会安装系统功能更新。'
      : (state.updatesMessage || 'Windows Update 驱动源只提供微软认证驱动。'));
    updateSelectionUi();
  }

  function updateSelectionUi() {
    const count = state.selectedUpdates.size;
    setText('#selectedUpdateCount', count ? `已选择 ${count} 个更新` : '未选择更新');
    for (const id of ['#installSelectedBtn', '#installSelectedBtnFoot']) {
      const button = $(id);
      if (button) {
        button.disabled = count === 0 || state.installing || !state.isAdmin;
        button.innerHTML = state.installing ? `${icon('refresh')}安装中…` : `${icon('download')}安装选中`;
      }
    }
    const selectAll = $('#selectAllUpdates');
    if (selectAll) {
      selectAll.checked = state.updates.length > 0 && count === state.updates.length;
      selectAll.indeterminate = count > 0 && count < state.updates.length;
    }
  }

  function toggleSelectAll() {
    if (state.selectedUpdates.size === state.updates.length) state.selectedUpdates.clear();
    else state.selectedUpdates = new Set(state.updates.map(item => item.id));
    renderUpdates();
  }

  /* ------------------------------------------------------------------ */
  /* progress                                                           */
  /* ------------------------------------------------------------------ */
  function applyScanProgress(progress) {
    state.scanProgress = progress;
    updateScanProgressUi();
  }

  function updateScanProgressUi() {
    if (!state.scanProgress) return;
    const percent = Math.max(0, Math.min(100, Number(state.scanProgress.percent) || 0));
    setText('#scanProgressPercent', `${percent}%`);
    setText('#scanProgressLabel', state.scanProgress.message || '正在扫描设备…');
    const bar = $('#scanProgressBar');
    if (bar) bar.style.width = `${percent}%`;
  }

  function showScanProgress(show, delay = 0) {
    const card = $('#scanProgressCard');
    if (!card) return;
    if (show) {
      card.hidden = false;
      updateScanProgressUi();
    } else {
      window.setTimeout(() => { card.hidden = true; }, delay);
    }
  }

  function applyUpdateProgress(progress) {
    state.updatesLoading = true;
    state.updateProgress = progress;
    updateUpdateProgressUi();
    if (state.activePage === 'updates') renderUpdates();
  }

  function applyInstallProgress(progress) {
    state.installProgress = progress;
    updateUpdateProgressUi();
    renderUpdates();
  }

  function updateUpdateProgressUi() {
    const progress = state.installProgress || state.updateProgress;
    const card = $('#updateProgressCard');
    if (!card || !progress) return;
    card.hidden = false;
    const percent = Math.max(0, Math.min(100, Number(progress.percent) || 0));
    setText('#updateProgressLabel', progress.message || (progress.stage === 'install' ? '正在安装驱动…' : '正在查询驱动更新…'));
    setText('#updateProgressPercent', `${percent}%`);
    const bar = $('#updateProgressBar');
    if (bar) bar.style.width = `${percent}%`;
  }

  function showUpdateProgress(show, delay = 0) {
    const card = $('#updateProgressCard');
    if (!card) return;
    if (show) {
      card.hidden = false;
      updateUpdateProgressUi();
    } else {
      window.setTimeout(() => { card.hidden = true; }, delay);
    }
  }

  /* ------------------------------------------------------------------ */
  /* render: system / settings / admin                                  */
  /* ------------------------------------------------------------------ */
  function renderSystem() {
    if (!state.system) return;
    setText('#settingsComputer', state.system.computerName || '--');
    setText('#settingsOs', state.system.osVersion || 'Windows');
    setText('#settingsDataPath', state.system.dataDirectory || '%LOCALAPPDATA%\\QuJie');
    setText('#settingsLogPath', state.system.logFile || 'app.log');
    setText('#settingsVersion', state.system.appVersion || '1.0.0');
    setText('#sidebarVersion', `驱界 v${state.system.appVersion || '1.0.0'}`);
  }

  function renderAdmin() {
    const admin = state.isAdmin;
    const pill = $('#adminPill');
    if (pill) pill.classList.toggle('is-warning', !admin);
    setText('#adminPillText', admin ? '管理员' : '标准权限');
    setText('#sidebarAdmin', admin ? 'UAC 管理员' : '标准权限');
    setText('#settingsAdminBadge', admin ? '管理员' : '标准权限');
    setText('#settingsAdminText', admin ? '已通过 Windows UAC 获得管理员权限' : '未获得管理员权限，驱动安装不可用');
  }

  function renderSettings() {
    const settings = state.settings || {};
    const auto = $('#settingAutoScan');
    const years = $('#settingOldYears');
    if (auto) auto.checked = settings.autoScan !== false;
    if (years) years.value = String(settings.oldDriverYears || 5);
    applyThemeOptions();
  }

  function applyThemeOptions() {
    $$('#themeOptions .theme-option').forEach(button => {
      button.classList.toggle('is-active', button.dataset.themeChoice === state.themeMode);
    });
  }

  function applyTheme(mode, save = false) {
    state.themeMode = mode === 'light' || mode === 'system' ? mode : 'dark';
    const effective = state.themeMode === 'system'
      ? (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark')
      : state.themeMode;
    document.documentElement.dataset.theme = effective;
    state.settings.theme = state.themeMode;
    applyThemeOptions();
    if (save) saveSettings();
  }

  function saveSettings() {
    state.settings.autoScan = $('#settingAutoScan')?.checked !== false;
    state.settings.oldDriverYears = Number($('#settingOldYears')?.value) || 5;
    state.settings.theme = state.themeMode;
    call('saveSettings', state.settings).catch(error => toast('保存设置失败', error.message, 'error'));
  }

  function initSystemTheme() {
    if (!window.matchMedia) return;
    const media = window.matchMedia('(prefers-color-scheme: light)');
    const handler = () => { if (state.themeMode === 'system') applyTheme('system', false); };
    if (media.addEventListener) media.addEventListener('change', handler);
    else if (media.addListener) media.addListener(handler);
  }

  function updateTopStatus(text) {
    if (text) {
      setText('#topStatus', text);
      return;
    }
    if (state.scanning) setText('#topStatus', '正在扫描设备与驱动');
    else if (state.updatesLoading) setText('#topStatus', '正在查询 Windows Update 驱动');
    else if (state.system) setText('#topStatus', `${state.isAdmin ? '管理员权限' : '标准权限'} · ${state.scan ? state.scan.totalDevices : '--'} 个在线设备`);
  }

  /* ------------------------------------------------------------------ */
  /* navigation / events                                                */
  /* ------------------------------------------------------------------ */
  function setTab(tab) {
    const allowed = ['overview', 'devices', 'updates', 'settings'];
    if (!allowed.includes(tab)) tab = 'overview';
    state.activePage = tab;
    $$('.page').forEach(page => {
      const active = page.dataset.page === tab;
      page.hidden = !active;
      page.classList.toggle('is-active', active);
    });
    $$('.nav-item').forEach(item => item.classList.toggle('is-active', item.dataset.page === tab));
    const main = $('#mainScroll');
    if (main) main.scrollTop = 0;
    if (tab === 'overview') renderOverview();
    if (tab === 'devices') renderDevices();
    if (tab === 'updates') {
      renderUpdates();
      if (!state.updates.length && !state.updatesLoading && !state.scanning) {
        window.setTimeout(() => searchUpdates(false), 150);
      }
    }
    if (tab === 'settings') renderSettings();
  }

  function bindEvents() {
    document.addEventListener('click', event => {
      const nav = event.target.closest('[data-nav]');
      if (nav) setTab(nav.dataset.nav);
      const pageNav = event.target.closest('.nav-item');
      if (pageNav) setTab(pageNav.dataset.page);
      const win = event.target.closest('[data-window]');
      if (win) {
        const action = win.dataset.window;
        if (action === 'minimize') call('window.minimize').catch(() => {});
        else if (action === 'maximize') call('window.toggleMaximize').catch(() => {});
        else if (action === 'close') call('window.close').catch(() => {});
      }
      const drawerClose = event.target.closest('[data-drawer-close]');
      if (drawerClose) closeDrawer();
      if (event.target.matches('.modal-backdrop')) closeModal(false);
      if (event.target.matches('[data-modal-close]')) closeModal(false);
    });

    $('#themeToggle')?.addEventListener('click', () => {
      applyTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark', true);
    });

    $('#scanBtn')?.addEventListener('click', () => scanDrivers(true));
    $('#heroScanBtn')?.addEventListener('click', () => scanDrivers(true));
    $('#deviceScanBtn')?.addEventListener('click', () => scanDrivers(true));
    $('#deviceRefreshBtn')?.addEventListener('click', () => scanDrivers(true));
    $('#refreshUpdatesBtn')?.addEventListener('click', () => searchUpdates(true));
    $('#openWuBtn')?.addEventListener('click', openWindowsUpdate);
    $('#openWuFromUpdates')?.addEventListener('click', openWindowsUpdate);
    $('#installAllBtn')?.addEventListener('click', () => {
      state.selectedUpdates = new Set(state.updates.map(item => item.id));
      installSelected();
    });
    $('#heroUpdateBtn')?.addEventListener('click', () => {
      setTab('updates');
      window.setTimeout(() => {
        state.selectedUpdates = new Set(state.updates.map(item => item.id));
        renderUpdates();
      }, 100);
    });
    $('#installSelectedBtn')?.addEventListener('click', installSelected);
    $('#installSelectedBtnFoot')?.addEventListener('click', installSelected);
    $('#selectAllUpdates')?.addEventListener('change', toggleSelectAll);
    $('#importDriverBtn')?.addEventListener('click', importDriverFolder);
    $('#importDriverBtn2')?.addEventListener('click', importDriverFolder);
    $('#openDataBtn')?.addEventListener('click', () => call('openDataFolder').catch(error => toast('打开失败', error.message, 'error')));
    $('#openLogBtn')?.addEventListener('click', () => call('openLog').catch(error => toast('打开失败', error.message, 'error')));
    $('#openDeviceManagerBtn')?.addEventListener('click', openDeviceManager);
    $('#adminPill')?.addEventListener('click', () => {
      toast(state.isAdmin ? '管理员权限已启用' : '需要管理员权限', state.isAdmin ? '驱动安装与系统扫描功能可用。' : '请关闭应用，在 UAC 弹窗中点击“是”重新启动。', state.isAdmin ? 'success' : 'warning');
    });

    $$('#deviceFilters .filter-chip').forEach(chip => {
      chip.addEventListener('click', () => {
        state.deviceFilter = chip.dataset.filter;
        $$('#deviceFilters .filter-chip').forEach(item => item.classList.toggle('is-active', item === chip));
        renderDevices();
      });
    });
    $('#deviceSearch')?.addEventListener('input', event => {
      state.deviceSearch = event.target.value || '';
      renderDevices();
    });

    $$('#themeOptions .theme-option').forEach(button => {
      button.addEventListener('click', () => applyTheme(button.dataset.themeChoice, true));
    });
    $('#settingAutoScan')?.addEventListener('change', saveSettings);
    $('#settingOldYears')?.addEventListener('change', () => {
      saveSettings();
      if (state.scan) scanDrivers(false);
    });

    $('#topbar')?.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      if (event.target.closest('button, input, select, .admin-pill')) return;
      call('window.drag').catch(() => {});
    });
    $('#topbar')?.addEventListener('dblclick', event => {
      if (event.target.closest('button, input, select, .admin-pill')) return;
      call('window.toggleMaximize').catch(() => {});
    });

    document.addEventListener('keydown', event => {
      if (event.key === 'Escape') {
        if (!$('#modalRoot').hidden) closeModal(false);
        else if (!$('#drawerRoot').hidden) closeDrawer();
      }
    });
  }

  async function openWindowsUpdate() {
    try {
      const result = await call('openWindowsUpdate');
      if (result && result.ok === false) toast('无法打开 Windows 更新', result.message || '', 'warning');
    } catch (error) {
      toast('无法打开 Windows 更新', error.message, 'error');
    }
  }

  async function openDeviceManager() {
    try {
      const result = await call('openDeviceManager');
      if (!result.ok) toast('无法打开设备管理器', result.error || '', 'error');
    } catch (error) {
      toast('无法打开设备管理器', error.message, 'error');
    }
  }

  /* ------------------------------------------------------------------ */
  /* modal / toast / helpers                                            */
  /* ------------------------------------------------------------------ */
  function confirmDialog({ title, sub, body, okText = '确认', cancelText = '取消', icon, danger = false }) {
    return new Promise(resolve => {
      state.modalResolver = resolve;
      $('#modalTitle').textContent = title || '确认操作';
      $('#modalSub').textContent = sub || '';
      $('#modalBody').textContent = body || '';
      const modalIcon = $('#modalIcon');
      modalIcon.className = 'modal-icon' + (danger ? ' danger' : '');
      modalIcon.innerHTML = iconSvg(icon || 'info');
      $('#modalActions').innerHTML = `
        <button class="btn ghost" data-modal-action="cancel">${escapeHtml(cancelText)}</button>
        <button class="btn ${danger ? 'danger' : 'primary'}" data-modal-action="ok">${escapeHtml(okText)}</button>`;
      $$('#modalActions [data-modal-action]').forEach(button => {
        button.addEventListener('click', () => closeModal(button.dataset.modalAction === 'ok'));
      });
      $('#modalRoot').hidden = false;
      window.setTimeout(() => $('#modalActions [data-modal-action="ok"]')?.focus(), 30);
    });
  }

  function closeModal(result) {
    const root = $('#modalRoot');
    if (root.hidden) return;
    root.hidden = true;
    const resolver = state.modalResolver;
    state.modalResolver = null;
    if (resolver) resolver(Boolean(result));
  }

  function toast(title, message, kind = 'info', duration = 4000) {
    const host = $('#toastHost');
    if (!host) return;
    const node = document.createElement('div');
    node.className = `toast ${kind}`;
    const iconName = kind === 'success' ? 'check' : kind === 'error' ? 'alert' : kind === 'warning' ? 'alert' : 'info';
    node.innerHTML = `${iconSvg(iconName)}<div><b>${escapeHtml(title)}</b>${message ? `<span>${escapeHtml(message)}</span>` : ''}</div>`;
    host.appendChild(node);
    window.setTimeout(() => {
      node.classList.add('is-leaving');
      window.setTimeout(() => node.remove(), 230);
    }, duration);
  }

  function setBusyButtons() {
    const busy = state.scanning || state.installing;
    for (const id of ['#scanBtn', '#heroScanBtn', '#deviceScanBtn', '#deviceRefreshBtn']) {
      const button = $(id);
      if (!button) continue;
      button.disabled = busy;
      button.classList.toggle('busy', busy);
    }
    for (const id of ['#installSelectedBtn', '#installSelectedBtnFoot', '#installAllBtn']) {
      const button = $(id);
      if (button) button.disabled = busy || state.selectedUpdates.size === 0 || !state.isAdmin;
    }
    const scanLabel = $('#scanBtnLabel');
    if (scanLabel) scanLabel.textContent = state.scanning ? '正在扫描…' : '检查驱动';
    updateSelectionUi();
  }

  function setText(selector, value) {
    const node = $(selector);
    if (node) node.textContent = value == null ? '' : String(value);
  }

  function setBar(selector, value) {
    const node = $(selector);
    if (node) node.style.width = `${Math.max(0, Math.min(100, Number(value) || 0))}%`;
  }

  function iconSvg(name) {
    return `<svg class="icon"><use href="#i-${icons[name] || name}"></use></svg>`;
  }

  function initialOf(name) {
    const text = String(name || '?').replace(/[^a-zA-Z0-9\u4e00-\u9fa5]/g, '');
    return (text.slice(0, 1) || '?').toUpperCase();
  }

  function escapeHtml(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function formatClock(date) {
    if (!(date instanceof Date) || Number.isNaN(date.getTime())) return '--';
    return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
  }

  function formatBytes(bytes) {
    const value = Number(bytes) || 0;
    if (value < 1024) return `${Math.round(value)} B`;
    const units = ['KB', 'MB', 'GB'];
    let amount = value / 1024;
    let index = 0;
    while (amount >= 1024 && index < units.length - 1) {
      amount /= 1024;
      index++;
    }
    return `${amount.toFixed(amount >= 100 ? 0 : 1)} ${units[index]}`;
  }

  function detailCell(label, value) {
    return `<div class="detail-cell"><span>${escapeHtml(label)}</span><b title="${escapeHtml(String(value))}">${escapeHtml(String(value))}</b></div>`;
  }

  function detailLine(label, value) {
    return `<div class="detail-line"><span>${escapeHtml(label)}</span><b title="${escapeHtml(String(value))}">${escapeHtml(String(value))}</b></div>`;
  }
})();
