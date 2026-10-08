/**
 * OGM PrintAudit — Enterprise Dashboard JavaScript
 * Real-time SSE streaming, live analytics, Spooler inspection, and interactive filtering.
 */
(function () {
  'use strict';

  // --- DOM Element References ---
  const connEl = document.getElementById('conn');
  const clockEl = document.getElementById('clock');
  const filterEl = document.getElementById('filter');
  const filterClearEl = document.getElementById('filter-clear');

  // KPI Stats
  const statAgents = document.getElementById('stat-agents');
  const statOnline = document.getElementById('stat-online');
  const statAgentsSub = document.getElementById('stat-agents-sub');
  const statAgentsRatio = document.getElementById('stat-agents-ratio');
  const statAgentsBar = document.getElementById('stat-agents-bar');
  const statJobs = document.getElementById('stat-jobs');
  const statJobsToday = document.getElementById('stat-jobs-today');
  const statBytes = document.getElementById('stat-bytes');
  const statAvgSize = document.getElementById('stat-avg-size');
  const statPending = document.getElementById('stat-pending');
  const statFailed = document.getElementById('stat-failed');
  const statFailedWrap = document.getElementById('stat-failed-wrap');
  const statSyncBadge = document.getElementById('stat-sync-badge');
  const statPrinters = document.getElementById('stat-printers');

  // Tables & Counts
  const agentsBody = document.querySelector('#agents-table tbody');
  const jobsBody = document.querySelector('#jobs-table tbody');
  const agentsEmpty = document.getElementById('agents-empty');
  const jobsEmpty = document.getElementById('jobs-empty');
  const agentsCount = document.getElementById('agents-count');
  const jobsCount = document.getElementById('jobs-count');

  // Agent Filters & Search
  const agentsSearchEl = document.getElementById('agents-search');
  const agentsStatusTabs = document.getElementById('agents-status-tabs');

  // Job Filters & Controls
  const filterPrinterEl = document.getElementById('filter-printer');
  const filterMachineEl = document.getElementById('filter-machine');
  const filterDatatypeEl = document.getElementById('filter-datatype');
  const filterTimeEl = document.getElementById('filter-time');
  const sortJobsEl = document.getElementById('sort-jobs');
  const btnResetFilters = document.getElementById('btn-reset-filters');
  const btnExportCsv = document.getElementById('btn-export-csv');
  const btnExportJson = document.getElementById('btn-export-json');

  // Pagination
  const pageSizeEl = document.getElementById('page-size');
  const paginationInfoEl = document.getElementById('pagination-info');
  const paginationPagesEl = document.getElementById('pagination-pages');

  // Analytics & Breakdown
  const breakdownMachines = document.getElementById('breakdown-machines');
  const breakdownPrinters = document.getElementById('breakdown-printers');

  // Topbar Buttons
  const btnTheme = document.getElementById('btn-theme');
  const iconThemeMoon = document.getElementById('icon-theme-moon');
  const iconThemeSun = document.getElementById('icon-theme-sun');
  const btnSound = document.getElementById('btn-sound');
  const iconSoundOn = document.getElementById('icon-sound-on');
  const iconSoundOff = document.getElementById('icon-sound-off');
  const btnGuide = document.getElementById('btn-guide');
  const btnSysinfo = document.getElementById('btn-sysinfo');
  const btnRefresh = document.getElementById('btn-refresh');
  const iconRefresh = document.getElementById('icon-refresh');

  // Modals
  const modalJob = document.getElementById('modal-job');
  const modalGuide = document.getElementById('modal-guide');
  const modalSysinfo = document.getElementById('modal-sysinfo');

  // Toast Container
  const toastContainer = document.getElementById('toast-container');

  // --- State ---
  let state = { agents: [], jobs: [], onlineThresholdSeconds: 90 };
  let serverInfo = null;
  let previousJobCount = -1;

  let agentFilterText = '';
  let agentStatusFilter = 'all';

  let jobFilterText = '';
  let selectedPrinter = '';
  let selectedMachine = '';
  let selectedDatatype = '';
  let selectedTime = 'all';
  let selectedSort = 'newest';

  let currentPage = 1;
  let pageSize = 15;

  let soundEnabled = localStorage.getItem('ogm_sound') !== 'false';
  let currentTheme = localStorage.getItem('ogm_theme') || 'dark';

  // --- Sound Alert System (Web Audio API Synthesizer) ---
  let audioCtx = null;
  function playNotificationChime() {
    if (!soundEnabled) return;
    try {
      if (!audioCtx) {
        const AudioContext = window.AudioContext || window.webkitAudioContext;
        if (!AudioContext) return;
        audioCtx = new AudioContext();
      }
      if (audioCtx.state === 'suspended') {
        audioCtx.resume();
      }

      const now = audioCtx.currentTime;
      const osc1 = audioCtx.createOscillator();
      const osc2 = audioCtx.createOscillator();
      const gain = audioCtx.createGain();

      osc1.type = 'sine';
      osc1.frequency.setValueAtTime(587.33, now); // D5
      osc1.frequency.exponentialRampToValueAtTime(880, now + 0.12); // A5

      osc2.type = 'triangle';
      osc2.frequency.setValueAtTime(880, now);
      osc2.frequency.exponentialRampToValueAtTime(1174.66, now + 0.15); // D6

      gain.gain.setValueAtTime(0.001, now);
      gain.gain.linearRampToValueAtTime(0.18, now + 0.03);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.4);

      osc1.connect(gain);
      osc2.connect(gain);
      gain.connect(audioCtx.destination);

      osc1.start(now);
      osc2.start(now);
      osc1.stop(now + 0.42);
      osc2.stop(now + 0.42);
    } catch (e) {
      console.warn('Ses çalınamadı:', e);
    }
  }

  // --- Utilities ---
  function esc(value) {
    return String(value === null || value === undefined ? '' : value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function fmtDateTime(iso) {
    if (!iso) return '-';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '-' : d.toLocaleString('tr-TR');
  }

  function fmtRelative(iso) {
    if (!iso) return '-';
    const diff = Date.now() - Date.parse(iso);
    if (isNaN(diff)) return '-';
    const sec = Math.floor(diff / 1000);
    if (sec < 10) return 'şimdi';
    if (sec < 60) return sec + ' sn önce';
    const min = Math.floor(sec / 60);
    if (min < 60) return min + ' dk önce';
    const hour = Math.floor(min / 60);
    if (hour < 24) return hour + ' sa önce';
    const day = Math.floor(hour / 24);
    return day + ' gün önce';
  }

  function fmtBytes(bytes) {
    const n = Number(bytes) || 0;
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    if (n < 1024 * 1024 * 1024) return (n / (1024 * 1024)).toFixed(1) + ' MB';
    return (n / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
  }

  function isOnline(agent) {
    const last = Date.parse(agent.lastSeenUtc);
    if (isNaN(last)) return false;
    return (Date.now() - last) <= (state.onlineThresholdSeconds * 1000);
  }

  window.copyText = function (text, btnElement) {
    if (!navigator.clipboard) {
      const ta = document.createElement('textarea');
      ta.value = text;
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      document.body.removeChild(ta);
    } else {
      navigator.clipboard.writeText(text);
    }

    if (btnElement) {
      const origText = btnElement.innerHTML;
      btnElement.innerHTML = 'Kopyalandı!';
      btnElement.style.color = 'var(--online)';
      setTimeout(() => {
        btnElement.innerHTML = origText;
        btnElement.style.color = '';
      }, 1500);
    } else {
      showToast('Kopyalandı', 'Panoya kopyalandı: ' + text.slice(0, 32) + '…', 'info');
    }
  };

  // --- Document Icon Classifier ---
  function getDocIcon(docName) {
    const ext = (docName || '').split('.').pop().toLowerCase();
    if (ext === 'pdf') {
      return '<span class="doc-icon doc-icon-pdf" title="PDF Belgesi">PDF</span>';
    }
    if (['doc', 'docx', 'odt', 'rtf'].includes(ext)) {
      return '<span class="doc-icon doc-icon-word" title="Word Belgesi">DOC</span>';
    }
    if (['xls', 'xlsx', 'csv', 'ods'].includes(ext)) {
      return '<span class="doc-icon doc-icon-excel" title="Tablo Belgesi">XLS</span>';
    }
    return '<span class="doc-icon doc-icon-text" title="Belge">TXT</span>';
  }

  // --- Theme Management ---
  function applyTheme(theme) {
    currentTheme = theme;
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('ogm_theme', theme);

    if (theme === 'light') {
      iconThemeMoon.style.display = 'none';
      iconThemeSun.style.display = 'block';
    } else {
      iconThemeMoon.style.display = 'block';
      iconThemeSun.style.display = 'none';
    }
  }

  btnTheme.addEventListener('click', () => {
    applyTheme(currentTheme === 'dark' ? 'light' : 'dark');
  });

  // Sound Toggle
  function updateSoundUI() {
    if (soundEnabled) {
      iconSoundOn.style.display = 'block';
      iconSoundOff.style.display = 'none';
      btnSound.classList.remove('btn-outline');
      btnSound.classList.add('btn-primary');
    } else {
      iconSoundOn.style.display = 'none';
      iconSoundOff.style.display = 'block';
      btnSound.classList.remove('btn-primary');
      btnSound.classList.add('btn-outline');
    }
  }

  btnSound.addEventListener('click', () => {
    soundEnabled = !soundEnabled;
    localStorage.setItem('ogm_sound', soundEnabled);
    updateSoundUI();
    if (soundEnabled) {
      playNotificationChime();
      showToast('Ses Açıldı', 'Yeni yazdırma işlerinde ses çalınacaktır.', 'info');
    }
  });

  // --- Toast Notifications ---
  function showToast(title, message, type = 'info', jobId = null) {
    const toast = document.createElement('div');
    toast.className = 'toast';

    let iconSvg = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="6 9 6 2 18 2 18 9"></polyline><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"></path><rect x="6" y="14" width="12" height="8"></rect></svg>';
    if (type === 'warn') {
      iconSvg = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#f59e0b" stroke-width="2"><circle cx="12" cy="12" r="10"></circle><line x1="12" y1="8" x2="12" y2="12"></line><line x1="12" y1="16" x2="12.01" y2="16"></line></svg>';
    }

    toast.innerHTML = `
      <div class="toast-icon">${iconSvg}</div>
      <div class="toast-content">
        <div class="toast-title">${esc(title)}</div>
        <div class="toast-msg">${esc(message)}</div>
        ${jobId ? `<div class="toast-action" onclick="window.openJobModal('${esc(jobId)}');">Belgeyi İncele →</div>` : ''}
      </div>
      <button class="toast-close" onclick="this.parentElement.remove();">✕</button>
    `;

    toastContainer.appendChild(toast);

    setTimeout(() => {
      toast.style.opacity = '0';
      toast.style.transform = 'translateY(16px)';
      setTimeout(() => toast.remove(), 300);
    }, 6000);
  }

  // --- Render Agents ---
  function renderAgents() {
    const agents = state.agents || [];
    let onlineCount = 0;
    let pendingSum = 0;
    let failedSum = 0;

    const filtered = agents.filter(agent => {
      const online = isOnline(agent);
      if (agentStatusFilter === 'online' && !online) return false;
      if (agentStatusFilter === 'offline' && online) return false;
      if (agentStatusFilter === 'pending' && (Number(agent.pendingJobs) || 0) <= 0) return false;

      if (agentFilterText) {
        const text = [agent.machineName, agent.userName, agent.ipAddress, agent.osVersion].join(' ').toLowerCase();
        if (text.indexOf(agentFilterText) < 0) return false;
      }
      return true;
    });

    const rows = [];
    for (const agent of agents) {
      if (isOnline(agent)) onlineCount++;
      pendingSum += Number(agent.pendingJobs) || 0;
      failedSum += Number(agent.failedJobs) || 0;
    }

    for (const agent of filtered) {
      const online = isOnline(agent);
      const userInitial = (agent.userName || 'U').charAt(0).toUpperCase();

      rows.push(`
        <tr>
          <td>
            <span class="badge ${online ? 'online' : 'offline'}" style="padding: 3px 9px; font-size: 11px;">
              <span class="radar-dot"></span>
              ${online ? 'Çevrimiçi' : 'Çevrimdışı'}
            </span>
          </td>
          <td>
            <div class="machine-pill">
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <rect x="2" y="3" width="20" height="14" rx="2"></rect><line x1="8" y1="21" x2="16" y2="21"></line><line x1="12" y1="17" x2="12" y2="21"></line>
              </svg>
              <strong>${esc(agent.machineName)}</strong>
            </div>
          </td>
          <td>
            <div class="user-chip">
              <span class="user-avatar">${esc(userInitial)}</span>
              <span>${esc(agent.userName)}</span>
            </div>
          </td>
          <td>
            <span class="hash-copy" onclick="copyText('${esc(agent.ipAddress || '-')}', this);" title="IP Adresini Kopyala">
              ${esc(agent.ipAddress || '-')}
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <rect x="9" y="9" width="13" height="13" rx="2" ry="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path>
              </svg>
            </span>
          </td>
          <td><span class="os-pill">${esc(agent.osVersion || 'Windows')}</span></td>
          <td><span class="pill-format" style="font-size: 10px;">${esc(agent.agentVersion || 'v1.0')}</span></td>
          <td title="${esc(fmtDateTime(agent.lastSeenUtc))}">${esc(fmtRelative(agent.lastSeenUtc))}</td>
          <td class="num"><span style="color: ${agent.pendingJobs > 0 ? 'var(--warn)' : 'var(--text-muted)'}; font-weight: 700;">${Number(agent.pendingJobs) || 0}</span></td>
          <td class="num">${Number(agent.sentJobs) || 0}</td>
          <td class="num"><span style="color: ${agent.failedJobs > 0 ? 'var(--danger)' : 'var(--text-muted)'}; font-weight: 700;">${Number(agent.failedJobs) || 0}</span></td>
          <td>
            <span class="badge ${agent.captureEnabled !== false ? 'online' : 'offline'}" style="padding: 2px 7px; font-size: 10px;">
              ${agent.captureEnabled !== false ? 'İzleme Aktif' : 'Pasif'}
            </span>
          </td>
          <td style="text-align: right;">
            <button class="btn btn-outline btn-sm" onclick="filterJobsByMachine('${esc(agent.machineName)}');" title="Bu bilgisayarın işlerini filtrele">
              İşleri Gör →
            </button>
          </td>
        </tr>
      `);
    }

    agentsBody.innerHTML = rows.join('');
    agentsEmpty.style.display = rows.length ? 'none' : 'flex';
    agentsCount.textContent = `${agents.length} ajan (${onlineCount} çevrimiçi)`;

    // Update KPI Card 1
    statAgents.textContent = agents.length;
    statOnline.textContent = onlineCount;
    statAgentsSub.textContent = `${agents.length} izlenen bilgisayar`;
    const ratio = agents.length > 0 ? Math.round((onlineCount / agents.length) * 100) : 0;
    statAgentsRatio.textContent = `%${ratio}`;
    statAgentsBar.style.width = `${ratio}%`;

    // Update KPI Card 4
    statPending.textContent = pendingSum;
    statFailed.textContent = failedSum;
    if (failedSum > 0) {
      statFailedWrap.style.color = 'var(--danger)';
      statSyncBadge.className = 'badge offline';
      statSyncBadge.textContent = 'Hata Var';
    } else {
      statFailedWrap.style.color = '';
      statSyncBadge.className = 'badge online';
      statSyncBadge.textContent = 'Senkronize';
    }
  }

  window.filterJobsByMachine = function (machineName) {
    filterMachineEl.value = machineName;
    selectedMachine = machineName;
    currentPage = 1;
    renderJobs();
    filterMachineEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
  };

  // --- Dynamic Filters Populator ---
  function updateFilterOptions() {
    const jobs = state.jobs || [];

    // Printers list
    const printers = Array.from(new Set(jobs.map(j => j.printerName).filter(Boolean))).sort();
    const currentPrinter = filterPrinterEl.value;
    filterPrinterEl.innerHTML = '<option value="">Tüm Yazıcılar</option>' +
      printers.map(p => `<option value="${esc(p)}" ${p === currentPrinter ? 'selected' : ''}>${esc(p)}</option>`).join('');

    // Machines list
    const machines = Array.from(new Set(jobs.map(j => j.machineName).filter(Boolean))).sort();
    const currentMachine = filterMachineEl.value;
    filterMachineEl.innerHTML = '<option value="">Tüm Bilgisayarlar</option>' +
      machines.map(m => `<option value="${esc(m)}" ${m === currentMachine ? 'selected' : ''}>${esc(m)}</option>`).join('');

    statPrinters.textContent = printers.length;
  }

  // --- Render Jobs ---
  function renderJobs() {
    const all = state.jobs || [];
    let totalBytesSum = 0;
    let todayCount = 0;
    const todayStr = new Date().toDateString();

    for (const job of all) {
      totalBytesSum += Number(job.totalBytes) || 0;
      if (job.receivedUtc && new Date(job.receivedUtc).toDateString() === todayStr) {
        todayCount++;
      }
    }

    statJobs.textContent = all.length;
    statJobsToday.textContent = `Bugün: ${todayCount} iş`;
    statBytes.textContent = fmtBytes(totalBytesSum);
    statAvgSize.textContent = all.length > 0 ? `Ort: ${fmtBytes(Math.round(totalBytesSum / all.length))}` : 'Ort: 0 B';

    // Filter pipeline
    let filtered = all.filter(job => {
      if (selectedPrinter && job.printerName !== selectedPrinter) return false;
      if (selectedMachine && job.machineName !== selectedMachine) return false;
      if (selectedDatatype && (job.dataType || '').toUpperCase().indexOf(selectedDatatype) < 0) return false;

      if (selectedTime !== 'all') {
        const jobTime = Date.parse(job.receivedUtc);
        const now = Date.now();
        if (selectedTime === '1h' && (now - jobTime) > 3600 * 1000) return false;
        if (selectedTime === 'today' && new Date(job.receivedUtc).toDateString() !== todayStr) return false;
        if (selectedTime === '7d' && (now - jobTime) > 7 * 86400 * 1000) return false;
      }

      if (jobFilterText) {
        const text = [job.machineName, job.userName, job.printerName, job.documentName, job.dataType, job.sha256]
          .join(' ')
          .toLowerCase();
        if (text.indexOf(jobFilterText) < 0) return false;
      }

      return true;
    });

    // Sorting
    filtered.sort((a, b) => {
      if (selectedSort === 'oldest') {
        return Date.parse(a.receivedUtc) - Date.parse(b.receivedUtc);
      }
      if (selectedSort === 'largest') {
        return (Number(b.totalBytes) || 0) - (Number(a.totalBytes) || 0);
      }
      if (selectedSort === 'pages') {
        return (Number(b.totalPages) || 0) - (Number(a.totalPages) || 0);
      }
      return Date.parse(b.receivedUtc) - Date.parse(a.receivedUtc); // newest
    });

    jobsCount.textContent = `${filtered.length} iş`;

    // Filter Reset visibility
    const isFiltered = jobFilterText || selectedPrinter || selectedMachine || selectedDatatype || selectedTime !== 'all';
    btnResetFilters.style.display = isFiltered ? 'inline-flex' : 'none';
    filterClearEl.style.display = jobFilterText ? 'block' : 'none';

    // Pagination
    const totalItems = filtered.length;
    let paginated = filtered;
    if (pageSize !== 'all') {
      const ps = Number(pageSize);
      const totalPages = Math.max(1, Math.ceil(totalItems / ps));
      if (currentPage > totalPages) currentPage = totalPages;
      const start = (currentPage - 1) * ps;
      paginated = filtered.slice(start, start + ps);

      paginationInfoEl.textContent = totalItems > 0
        ? `${start + 1} - ${Math.min(start + ps, totalItems)} / ${totalItems} iş gösteriliyor`
        : '0 iş gösteriliyor';

      renderPaginationControls(totalPages);
    } else {
      paginationInfoEl.textContent = `Toplam ${totalItems} iş gösteriliyor`;
      paginationPagesEl.innerHTML = '';
    }

    // Render table rows
    const rows = paginated.map(job => {
      const userInitial = (job.userName || 'U').charAt(0).toUpperCase();
      const docIcon = getDocIcon(job.documentName);
      const shaShort = (job.sha256 || '').slice(0, 10);
      const jobIdEnc = encodeURIComponent(job.jobId);

      // PDF yalnizca gercekten donusturulebilen (PDF/XPS) girdiler icin
      // sunulur; gorsel/EMF/RAW islerde ham veri indirme gosterilir.
      const pdfActions = job.hasPdf
        ? `<a class="btn btn-success btn-sm" href="/api/jobs/${jobIdEnc}/pdf" target="_blank" title="PDF Olarak Aç ve İçeriği Gör">
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path><circle cx="12" cy="12" r="3"></circle>
                </svg>
                <span>Görüntüle</span>
              </a>
              <a class="btn btn-primary btn-sm" href="/api/jobs/${jobIdEnc}/download-pdf" title="PDF Olarak İndir">
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"></path><polyline points="14 2 14 8 20 8"></polyline><line x1="16" y1="13" x2="8" y2="13"></line><line x1="16" y1="17" x2="8" y2="17"></line>
                </svg>
                <span>PDF İndir</span>
              </a>`
        : `<a class="btn btn-primary btn-sm" href="/api/jobs/${jobIdEnc}/payload" download title="Ham Yazdırma Verisini İndir">
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><polyline points="7 10 12 15 17 10"></polyline><line x1="12" y1="15" x2="12" y2="3"></line>
                </svg>
                <span>Ham Veri</span>
              </a>`;

      return `
        <tr>
          <td>
            <div style="display: flex; flex-direction: column;">
              <span style="font-weight: 600;" title="${esc(fmtDateTime(job.receivedUtc))}">${esc(fmtRelative(job.receivedUtc))}</span>
              <span style="font-size: 11px; color: var(--text-dim);">${esc(fmtDateTime(job.receivedUtc))}</span>
            </div>
          </td>
          <td>
            <div style="display: flex; flex-direction: column; gap: 2px;">
              <div class="user-chip">
                <span class="user-avatar">${esc(userInitial)}</span>
                <span>${esc(job.userName)}</span>
              </div>
              <span style="font-size: 11px; color: var(--text-dim); margin-left: 31px;">${esc(job.machineName)}</span>
            </div>
          </td>
          <td>
            <div style="display: flex; align-items: center; gap: 6px; font-weight: 600;">
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <polyline points="6 9 6 2 18 2 18 9"></polyline><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"></path><rect x="6" y="14" width="12" height="8"></rect>
              </svg>
              <span>${esc(job.printerName)}</span>
            </div>
          </td>
          <td>
            <div class="doc-name">
              ${docIcon}
              <span title="${esc(job.documentName)}">${esc(job.documentName || 'İsimsiz Belge')}</span>
            </div>
          </td>
          <td>
            <div style="display: flex; align-items: center; gap: 6px;">
              <span class="pill-format">${esc(job.dataType || 'RAW')}</span>
              <span style="font-size: 11px; color: var(--text-muted);">${Number(job.totalPages) || 1} sf</span>
            </div>
          </td>
          <td class="num">
            <span class="pill-size">${esc(fmtBytes(job.totalBytes))}</span>
          </td>
          <td>
            <span class="hash-copy" onclick="copyText('${esc(job.sha256)}', this);" title="SHA-256 Karmasını Kopyala">
              ${esc(shaShort)}…
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <rect x="9" y="9" width="13" height="13" rx="2" ry="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path>
              </svg>
            </span>
          </td>
          <td>
            <div class="action-group">
              ${pdfActions}
              <button class="btn btn-outline btn-sm" onclick="window.openJobModal('${esc(job.jobId)}');" title="İçeriği ve Detayları İncele">
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <circle cx="11" cy="11" r="8"></circle><line x1="21" y1="21" x2="16.65" y2="16.65"></line>
                </svg>
                <span>Detay</span>
              </button>
            </div>
          </td>
        </tr>
      `;
    });

    jobsBody.innerHTML = rows.join('');
    jobsEmpty.style.display = rows.length ? 'none' : 'flex';
  }

  function renderPaginationControls(totalPages) {
    const pages = [];
    pages.push(`
      <button class="page-btn" ${currentPage === 1 ? 'disabled' : ''} onclick="goToPage(${currentPage - 1});" title="Önceki Sayfa">‹</button>
    `);

    let start = Math.max(1, currentPage - 2);
    let end = Math.min(totalPages, currentPage + 2);

    if (start > 1) {
      pages.push(`<button class="page-btn" onclick="goToPage(1);">1</button>`);
      if (start > 2) pages.push(`<span style="padding: 0 4px; color: var(--text-dim);">…</span>`);
    }

    for (let i = start; i <= end; i++) {
      pages.push(`
        <button class="page-btn ${i === currentPage ? 'active' : ''}" onclick="goToPage(${i});">${i}</button>
      `);
    }

    if (end < totalPages) {
      if (end < totalPages - 1) pages.push(`<span style="padding: 0 4px; color: var(--text-dim);">…</span>`);
      pages.push(`<button class="page-btn" onclick="goToPage(${totalPages});">${totalPages}</button>`);
    }

    pages.push(`
      <button class="page-btn" ${currentPage === totalPages ? 'disabled' : ''} onclick="goToPage(${currentPage + 1});" title="Sonraki Sayfa">›</button>
    `);

    paginationPagesEl.innerHTML = pages.join('');
  }

  window.goToPage = function (page) {
    currentPage = page;
    renderJobs();
  };

  // --- Render Visual Analytics ---
  function renderAnalytics() {
    const jobs = state.jobs || [];

    // 1) Activity Timeline Chart (Last 12 hours)
    const hoursCount = 12;
    const now = Date.now();
    const buckets = new Array(hoursCount).fill(0);
    const bucketLabels = [];

    for (let i = hoursCount - 1; i >= 0; i--) {
      const d = new Date(now - i * 3600 * 1000);
      bucketLabels.push(d.getHours() + ':00');
    }

    for (const job of jobs) {
      const t = Date.parse(job.receivedUtc);
      if (!isNaN(t)) {
        const diffHours = Math.floor((now - t) / (3600 * 1000));
        if (diffHours >= 0 && diffHours < hoursCount) {
          buckets[hoursCount - 1 - diffHours]++;
        }
      }
    }

    const maxVal = Math.max(...buckets, 4);
    const width = 500;
    const height = 110;
    const padding = 15;
    const step = (width - padding * 2) / (hoursCount - 1);

    const points = buckets.map((val, idx) => {
      const x = padding + idx * step;
      const y = height - (val / maxVal) * (height - 25);
      return { x, y, val, label: bucketLabels[idx] };
    });

    if (points.length > 0) {
      let linePath = `M ${points[0].x} ${points[0].y}`;
      for (let i = 1; i < points.length; i++) {
        linePath += ` L ${points[i].x} ${points[i].y}`;
      }

      const areaPath = `${linePath} L ${points[points.length - 1].x} ${height} L ${points[0].x} ${height} Z`;

      document.getElementById('chart-area').setAttribute('d', areaPath);
      document.getElementById('chart-line').setAttribute('d', linePath);

      const pointsContainer = document.getElementById('chart-points');
      pointsContainer.innerHTML = points.map(p => `
        <circle cx="${p.x}" cy="${p.y}" r="3.5" fill="#3b82f6" stroke="#ffffff" stroke-width="1.5">
          <title>${p.label} - ${p.val} iş</title>
        </circle>
      `).join('');
    }

    // 2) Top Machines Breakdown
    const machineCounts = {};
    for (const job of jobs) {
      const m = job.machineName || 'Bilinmiyor';
      machineCounts[m] = (machineCounts[m] || 0) + 1;
    }
    const sortedMachines = Object.entries(machineCounts).sort((a, b) => b[1] - a[1]).slice(0, 4);

    if (sortedMachines.length > 0) {
      const total = jobs.length || 1;
      breakdownMachines.innerHTML = sortedMachines.map(([name, count]) => {
        const pct = Math.round((count / total) * 100);
        return `
          <div class="breakdown-item">
            <div class="breakdown-info">
              <span class="breakdown-name" title="${esc(name)}">${esc(name)}</span>
              <span class="breakdown-count">${count} iş (%${pct})</span>
            </div>
            <div class="breakdown-track">
              <div class="breakdown-fill" style="width: ${pct}%; background: var(--accent);"></div>
            </div>
          </div>
        `;
      }).join('');
    } else {
      breakdownMachines.innerHTML = '<div style="color: var(--text-dim); font-size: 12px; text-align: center; padding: 20px 0;">Henüz iş yok</div>';
    }

    // 3) Printer Usage Breakdown
    const printerCounts = {};
    for (const job of jobs) {
      const p = job.printerName || 'Bilinmiyor';
      printerCounts[p] = (printerCounts[p] || 0) + 1;
    }
    const sortedPrinters = Object.entries(printerCounts).sort((a, b) => b[1] - a[1]).slice(0, 4);

    if (sortedPrinters.length > 0) {
      const total = jobs.length || 1;
      const colors = ['#10b981', '#06b6d4', '#8b5cf6', '#f59e0b'];
      breakdownPrinters.innerHTML = sortedPrinters.map(([name, count], idx) => {
        const pct = Math.round((count / total) * 100);
        const col = colors[idx % colors.length];
        return `
          <div class="breakdown-item">
            <div class="breakdown-info">
              <span class="breakdown-name" title="${esc(name)}">${esc(name)}</span>
              <span class="breakdown-count">${count} iş (%${pct})</span>
            </div>
            <div class="breakdown-track">
              <div class="breakdown-fill" style="width: ${pct}%; background: ${col};"></div>
            </div>
          </div>
        `;
      }).join('');
    } else {
      breakdownPrinters.innerHTML = '<div style="color: var(--text-dim); font-size: 12px; text-align: center; padding: 20px 0;">Henüz iş yok</div>';
    }
  }

  // --- Master Render ---
  function render() {
    renderAgents();
    updateFilterOptions();
    renderJobs();
    renderAnalytics();
  }

  function applyState(next) {
    if (!next) return;

    // Tam durum anlik goruntusu (baglanti kurulunca veya sifirlama sonrasi).
    // Yeni is bildirimleri artik artimsal JobReceived olaylariyla verilir;
    // burada yeniden bildirim gosterilmez.
    state = next;
    previousJobCount = (next.jobs || []).length;
    render();
  }

  // --- Initial State & SSE Streaming ---
  async function loadInitialState() {
    try {
      const response = await fetch('/api/state', { cache: 'no-store' });
      if (response.ok) {
        const data = await response.json();
        applyState(data);
      }
    } catch (err) {
      console.warn('Durum yüklenemedi', err);
    }
  }

  async function loadServerInfo() {
    try {
      const res = await fetch('/api/server/info', { cache: 'no-store' });
      if (res.ok) {
        serverInfo = await res.json();
        updateServerInfoUI(serverInfo);
      }
    } catch (err) {
      console.warn('Sunucu bilgisi alınamadı', err);
    }
  }

  function updateServerInfoUI(info) {
    if (!info) return;

    // Guide Modal IPs
    const ipListEl = document.getElementById('server-ip-list');
    const ips = info.localIps && info.localIps.length ? info.localIps : ['127.0.0.1'];

    ipListEl.innerHTML = ips.map(ip => `
      <div class="code-snippet">
        <span>http://${esc(ip)}:${info.port || 5099}</span>
        <button class="btn btn-outline btn-sm" onclick="copyText('http://${esc(ip)}:${info.port || 5099}', this);">Kopyala</button>
      </div>
    `).join('');

    // SysInfo Modal
    document.getElementById('sys-version').textContent = info.version || '1.0.0';
    document.getElementById('sys-uptime').textContent = `${Math.floor((info.uptimeSeconds || 0) / 60)} dakika`;
    document.getElementById('sys-threshold').textContent = `${info.onlineThresholdSeconds || 90} saniye`;
    document.getElementById('sys-ips').textContent = ips.join(', ');
  }

  function handleServerEvent(evt) {
    if (!evt || !evt.kind) return;

    // Tam durum anlik goruntusu.
    if (evt.kind === 'Snapshot' || evt.kind === 'StateSnapshot') {
      if (evt.state) applyState(evt.state);
      return;
    }

    // Artimsal olaylar: yalnizca degisen varlik gonderilir.
    if (evt.kind === 'JobReceived' && evt.job) {
      const job = evt.job;
      const list = state.jobs || [];
      state.jobs = [job, ...list.filter(j => j.jobId !== job.jobId)];
      previousJobCount = state.jobs.length;

      showToast(
        'Yeni Yazdırma İşi!',
        `"${job.documentName}" (${job.machineName} - ${fmtBytes(job.totalBytes)})`,
        'info',
        job.jobId
      );
      playNotificationChime();
      render();
      return;
    }

    if ((evt.kind === 'AgentRegistered' || evt.kind === 'AgentHeartbeat') && evt.agent) {
      const agent = evt.agent;
      const agents = (state.agents || []).filter(a => a.agentId !== agent.agentId);
      agents.push(agent);
      state.agents = agents;
      render();
    }
  }

  function connectStream() {
    const source = new EventSource('/api/events');

    source.onopen = function () {
      connEl.className = 'badge online';
      connEl.querySelector('.conn-text').textContent = 'Canlı Akış (SSE)';
    };

    source.onerror = function () {
      connEl.className = 'badge offline';
      connEl.querySelector('.conn-text').textContent = 'bağlantı koptu';
    };

    source.onmessage = function (event) {
      try {
        handleServerEvent(JSON.parse(event.data));
      } catch (err) {
        console.warn('Olay çözümlenemedi', err);
      }
    };
  }

  // --- Job Detail & Preview Modal ---
  window.openJobModal = async function (jobId) {
    const job = (state.jobs || []).find(j => j.jobId === jobId);
    if (!job) return;

    // Header & Titles
    document.getElementById('modal-doc-name').textContent = job.documentName || 'İsimsiz Belge';
    document.getElementById('modal-doc-subtitle').textContent = `İş ID: ${job.jobId} • Makine: ${job.machineName}`;
    document.getElementById('modal-format-badge').textContent = job.dataType || 'RAW';

    // Tab 1 Metadata
    document.getElementById('md-doc-name').textContent = job.documentName || '-';
    document.getElementById('md-printer').textContent = job.printerName || '-';
    document.getElementById('md-machine').textContent = job.machineName || '-';
    document.getElementById('md-user').textContent = job.userName || '-';
    document.getElementById('md-format-pages').textContent = `${job.dataType || 'RAW'} • ${job.totalPages || 1} Sayfa`;
    document.getElementById('md-bytes').textContent = `${fmtBytes(job.totalBytes)} (${Number(job.totalBytes).toLocaleString('tr-TR')} bayt)`;
    document.getElementById('md-date').textContent = fmtDateTime(job.receivedUtc);
    document.getElementById('md-agent-id').textContent = job.agentId || '-';
    document.getElementById('md-sha256').textContent = job.sha256 || '-';

    // PDF ve SPL İndirme / Görüntüleme Butonları
    const viewPdfBtn = document.getElementById('modal-view-pdf-btn');
    const dlPdfBtn = document.getElementById('modal-download-pdf-btn');
    const dlSplBtn = document.getElementById('modal-download-link');

    viewPdfBtn.href = `/api/jobs/${encodeURIComponent(job.jobId)}/pdf`;
    dlPdfBtn.href = `/api/jobs/${encodeURIComponent(job.jobId)}/download-pdf`;
    dlSplBtn.href = `/api/jobs/${encodeURIComponent(job.jobId)}/payload`;

    // PDF butonlari yalnizca donusturulebilir (PDF/XPS) islerde gorunur.
    viewPdfBtn.style.display = job.hasPdf ? '' : 'none';
    dlPdfBtn.style.display = job.hasPdf ? '' : 'none';
    dlSplBtn.setAttribute('download', `${job.jobId}.spl`);
    document.getElementById('modal-download-label').textContent = `Ham SPL (${fmtBytes(job.totalBytes)})`;

    // Reset Tab to Tab 1
    window.switchModalTab('meta');

    // Open Modal
    modalJob.classList.add('active');

    // Load Preview Asynchronously
    document.getElementById('preview-detected-format').textContent = 'Analiz ediliyor…';
    document.getElementById('preview-text-box').textContent = 'Spool dosyasından metinler ayıklanıyor…';
    document.getElementById('preview-hex-box').textContent = 'Hex dökümü hazırlanıyor…';

    try {
      const res = await fetch(`/api/jobs/${encodeURIComponent(job.jobId)}/preview`);
      if (res.ok) {
        const preview = await res.json();
        document.getElementById('preview-detected-format').textContent = preview.detectedFormat || 'Ham Veri';

        const lines = (preview.extractedStrings && preview.extractedStrings.length > 0)
          ? preview.extractedStrings.join('\n')
          : '(Spool dosyasında okunabilir metin bulunamadı veya dosya saf raster grafik içeriyor)';

        document.getElementById('preview-text-box').textContent = lines;
        document.getElementById('preview-hex-box').textContent = preview.hexDump || '-';

        document.getElementById('btn-copy-extracted').onclick = function () {
          copyText(lines, this);
        };
      } else {
        document.getElementById('preview-detected-format').textContent = 'Bilinmiyor';
        document.getElementById('preview-text-box').textContent = '(Önizleme sunucudan alınamadı)';
        document.getElementById('preview-hex-box').textContent = '-';
      }
    } catch (e) {
      document.getElementById('preview-detected-format').textContent = 'Hata';
      document.getElementById('preview-text-box').textContent = 'Önizleme yüklenirken bir sorun oluştu.';
    }
  };

  window.closeJobModal = function () {
    modalJob.classList.remove('active');
  };

  window.switchModalTab = function (tab) {
    const tabMeta = document.getElementById('modal-tab-meta');
    const tabPreview = document.getElementById('modal-tab-preview');
    const paneMeta = document.getElementById('modal-pane-meta');
    const panePreview = document.getElementById('modal-pane-preview');

    if (tab === 'meta') {
      tabMeta.classList.add('active');
      tabPreview.classList.remove('active');
      paneMeta.style.display = 'block';
      panePreview.style.display = 'none';
    } else {
      tabPreview.classList.add('active');
      tabMeta.classList.remove('active');
      paneMeta.style.display = 'none';
      panePreview.style.display = 'flex';
    }
  };

  // Guide Modal
  window.openGuideModal = function () {
    loadServerInfo();
    modalGuide.classList.add('active');
  };

  window.closeGuideModal = function () {
    modalGuide.classList.remove('active');
  };

  btnGuide.addEventListener('click', window.openGuideModal);

  // Sysinfo Modal
  window.openSysinfoModal = function () {
    loadServerInfo();
    modalSysinfo.classList.add('active');
  };

  window.closeSysinfoModal = function () {
    modalSysinfo.classList.remove('active');
  };

  btnSysinfo.addEventListener('click', window.openSysinfoModal);

  // Close modals when clicking overlay
  [modalJob, modalGuide, modalSysinfo].forEach(modal => {
    modal.addEventListener('click', function (e) {
      if (e.target === modal) {
        modal.classList.remove('active');
      }
    });
  });

  // Global ESC handler
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      modalJob.classList.remove('active');
      modalGuide.classList.remove('active');
      modalSysinfo.classList.remove('active');
    } else if (e.key === '/' && document.activeElement !== filterEl && document.activeElement !== agentsSearchEl) {
      e.preventDefault();
      filterEl.focus();
    }
  });

  // --- Exporting Functions ---
  btnExportCsv.addEventListener('click', () => {
    const all = state.jobs || [];
    if (!all.length) {
      showToast('Uyarı', 'Dışa aktarılacak iş bulunamadı.', 'warn');
      return;
    }

    const headers = ['Zaman', 'IsID', 'Makine', 'Kullanici', 'Yazici', 'BelgeAdi', 'Format', 'Boyut_Bayt', 'Sayfa', 'SHA256'];
    const rows = all.map(j => [
      `"${fmtDateTime(j.receivedUtc)}"`,
      `"${j.jobId}"`,
      `"${j.machineName}"`,
      `"${j.userName}"`,
      `"${j.printerName}"`,
      `"${(j.documentName || '').replace(/"/g, '""')}"`,
      `"${j.dataType}"`,
      j.totalBytes,
      j.totalPages,
      `"${j.sha256}"`
    ]);

    const csvContent = '\uFEFF' + [headers.join(','), ...rows.map(r => r.join(','))].join('\r\n');
    const blob = new Blob([csvContent], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `ogm-print-jobs-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
    showToast('Başarılı', 'CSV dosyası indirildi.', 'info');
  });

  btnExportJson.addEventListener('click', () => {
    const all = state.jobs || [];
    const blob = new Blob([JSON.stringify(all, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `ogm-print-jobs-${new Date().toISOString().slice(0, 10)}.json`;
    a.click();
    URL.revokeObjectURL(url);
    showToast('Başarılı', 'JSON dosyası indirildi.', 'info');
  });

  // --- Event Listeners for Filters ---
  filterEl.addEventListener('input', () => {
    jobFilterText = filterEl.value.trim().toLowerCase();
    currentPage = 1;
    renderJobs();
  });

  filterClearEl.addEventListener('click', () => {
    filterEl.value = '';
    jobFilterText = '';
    currentPage = 1;
    renderJobs();
    filterEl.focus();
  });

  filterPrinterEl.addEventListener('change', () => {
    selectedPrinter = filterPrinterEl.value;
    currentPage = 1;
    renderJobs();
  });

  filterMachineEl.addEventListener('change', () => {
    selectedMachine = filterMachineEl.value;
    currentPage = 1;
    renderJobs();
  });

  filterDatatypeEl.addEventListener('change', () => {
    selectedDatatype = filterDatatypeEl.value;
    currentPage = 1;
    renderJobs();
  });

  filterTimeEl.addEventListener('change', () => {
    selectedTime = filterTimeEl.value;
    currentPage = 1;
    renderJobs();
  });

  sortJobsEl.addEventListener('change', () => {
    selectedSort = sortJobsEl.value;
    renderJobs();
  });

  pageSizeEl.addEventListener('change', () => {
    pageSize = pageSizeEl.value;
    currentPage = 1;
    renderJobs();
  });

  btnResetFilters.addEventListener('click', () => {
    filterEl.value = '';
    jobFilterText = '';
    filterPrinterEl.value = '';
    selectedPrinter = '';
    filterMachineEl.value = '';
    selectedMachine = '';
    filterDatatypeEl.value = '';
    selectedDatatype = '';
    filterTimeEl.value = 'all';
    selectedTime = 'all';
    currentPage = 1;
    renderJobs();
  });

  // Agent Filters
  agentsSearchEl.addEventListener('input', () => {
    agentFilterText = agentsSearchEl.value.trim().toLowerCase();
    renderAgents();
  });

  agentsStatusTabs.addEventListener('click', e => {
    const tab = e.target.closest('.filter-tab');
    if (!tab) return;
    agentsStatusTabs.querySelectorAll('.filter-tab').forEach(t => t.classList.remove('active'));
    tab.classList.add('active');
    agentStatusFilter = tab.getAttribute('data-filter') || 'all';
    renderAgents();
  });

  // Manual Refresh Button
  btnRefresh.addEventListener('click', async () => {
    iconRefresh.classList.add('spin');
    await loadInitialState();
    await loadServerInfo();
    setTimeout(() => iconRefresh.classList.remove('spin'), 500);
  });

  // Live Clock updater (Turkish format with Day of Week)
  function updateClock() {
    const now = new Date();
    const options = {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit'
    };
    clockEl.textContent = now.toLocaleDateString('tr-TR', options);
  }
  setInterval(updateClock, 1000);
  updateClock();

  // Periodic re-render for relative times and online status calculations
  setInterval(render, 5000);

  // Initialize UI & stream
  applyTheme(currentTheme);
  updateSoundUI();
  loadInitialState().then(connectStream);
  loadServerInfo();
})();
