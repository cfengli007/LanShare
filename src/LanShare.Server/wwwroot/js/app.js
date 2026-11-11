/* LanShare index page: upload, result and "my shares". */
(function () {
  'use strict';

  var UI = window.LSUI;
  var $ = UI.$;
  var $$ = UI.$$;
  var escapeHtml = UI.escapeHtml;
  var formatBytes = UI.formatBytes;
  var formatDateTime = UI.formatDateTime;
  var formatRemaining = UI.formatRemaining;
  var formatExpiryLength = UI.formatExpiryLength;
  var parseTimestamp = UI.parseTimestamp;
  var fileVisual = UI.fileVisual;

  var PRESET_EXPIRY = [10, 60, 360, 1440, 10080];
  var MAX_CUSTOM_MINUTES = 43200;

  var PENDING_KEY = 'lanshare.pendingUpload.v1';
  var CHUNK_FALLBACK_BYTES = 8 * 1024 * 1024;
  var MAX_CHUNK_RETRIES = 3;
  var RETRY_DELAYS = [500, 1000, 2000];

  var DEFAULT_CONFIG = {
    maxUploadBytes: 2147483648,
    defaultExpiryMinutes: 1440,
    burnAfterDownload: false,
    port: 8910,
    internalBaseUrl: '',
    externalBaseUrl: '',
    productName: '内网快传',
    version: '1.0.0',
    lanAddresses: []
  };

  var state = {
    config: normalizeConfig(null),
    files: [],
    uploading: false,
    uploadController: null,
    uploadAbortRequested: false,
    pendingSession: null,
    pendingMeta: null,
    resumeLocalFiles: null,
    resumePickMode: false,
    shares: [],
    sharesLoading: false,
    sharesLoadedAt: 0,
    lastResult: null,
    revokeTarget: null,
    revokeModal: null,
    burnTouched: false,
    expiryTouched: false,
    expiredReloadScheduled: false
  };

  /* ------------------------------------------------------------------ */
  /* Small helpers                                                       */
  /* ------------------------------------------------------------------ */

  function totalBytes() {
    return state.files.reduce(function (sum, file) { return sum + (Number(file.size) || 0); }, 0);
  }

  function fileKey(file) {
    return file.name + ':' + file.size + ':' + (file.lastModified || 0);
  }

  function absoluteUrl(path) {
    try {
      return new URL(path, window.location.href).href;
    } catch (error) {
      return window.location.origin + path;
    }
  }

  function setVersion(version) {
    if (!version) return;
    var badge = $('#versionBadge');
    if (badge) badge.textContent = 'v' + String(version).replace(/^v/i, '');
  }

  function setStatus(status, text, title) {
    var pill = $('#statusPill');
    if (!pill) return;
    pill.setAttribute('data-state', status);
    $('#statusText').textContent = text;
    pill.title = title || '';
  }

  function formatUptime(seconds) {
    var total = Math.max(0, Math.floor(Number(seconds) || 0));
    if (total < 60) return '不到 1 分钟';
    var days = Math.floor(total / 86400);
    var hours = Math.floor((total % 86400) / 3600);
    var minutes = Math.floor((total % 3600) / 60);
    if (days > 0) return days + ' 天 ' + hours + ' 小时';
    if (hours > 0) return hours + ' 小时 ' + minutes + ' 分钟';
    return minutes + ' 分钟';
  }

  function createdLabel(value) {
    var time = parseTimestamp(value);
    if (time === null) return '时间未知';
    var diff = Date.now() - time;
    if (diff < 60000) return '刚刚创建';
    if (diff < 3600000) return Math.floor(diff / 60000) + ' 分钟前创建';
    if (diff < 86400000) return Math.floor(diff / 3600000) + ' 小时前创建';
    if (diff < 604800000) return Math.floor(diff / 86400000) + ' 天前创建';
    return formatDateTime(time) + ' 创建';
  }

  function normalizeConfig(raw) {
    var result = {};
    Object.keys(DEFAULT_CONFIG).forEach(function (key) { result[key] = DEFAULT_CONFIG[key]; });
    if (raw && typeof raw === 'object') {
      Object.keys(result).forEach(function (key) {
        if (raw[key] !== undefined && raw[key] !== null) result[key] = raw[key];
      });
    }
    result.maxUploadBytes = Number(result.maxUploadBytes);
    if (!isFinite(result.maxUploadBytes) || result.maxUploadBytes <= 0) {
      result.maxUploadBytes = DEFAULT_CONFIG.maxUploadBytes;
    }
    result.defaultExpiryMinutes = Number(result.defaultExpiryMinutes);
    if (!isFinite(result.defaultExpiryMinutes) || result.defaultExpiryMinutes <= 0) {
      result.defaultExpiryMinutes = DEFAULT_CONFIG.defaultExpiryMinutes;
    }
    result.lanAddresses = Array.isArray(result.lanAddresses)
      ? result.lanAddresses.filter(function (item) { return item && item.address; })
      : [];
    return result;
  }

  /* ------------------------------------------------------------------ */
  /* Config + service info                                               */
  /* ------------------------------------------------------------------ */

  async function loadConfig() {
    try {
      var raw = await UI.requestJson('/api/config');
      state.config = normalizeConfig(raw);
    } catch (error) {
      state.config = normalizeConfig(null);
      if (error && error.status >= 500) {
        UI.showToast('warning', '服务配置读取失败', '已使用默认上传设置');
      }
    }
    applyConfig();
    updateUploadState();
  }

  function applyConfig() {
    var cfg = state.config;
    var brandName = $('#brandName');
    brandName.textContent = cfg.productName || '内网快传';
    document.title = (cfg.productName || '内网快传') + ' · 局域网文件分享';

    $('#maxSizeHint').textContent = formatBytes(cfg.maxUploadBytes);

    if (!state.burnTouched) {
      $('#burnSwitch').checked = !!cfg.burnAfterDownload;
    }
    if (!state.expiryTouched) {
      initExpirySelection(cfg.defaultExpiryMinutes);
    }
    setVersion(cfg.version);
    $('#footerMeta').textContent = 'v' + String(cfg.version || '1.0.0').replace(/^v/i, '') + ' · 文件仅保存在本机 · 到期自动清理';
  }

  function initExpirySelection(minutes) {
    var value = Number(minutes);
    if (!isFinite(value) || value < 1) value = DEFAULT_CONFIG.defaultExpiryMinutes;
    var preset = PRESET_EXPIRY.indexOf(Math.round(value)) >= 0;
    var targetValue = preset ? String(Math.round(value)) : 'custom';
    var radio = document.querySelector('input[name="expiry"][value="' + targetValue + '"]');
    if (radio) radio.checked = true;
    $('#customMinutes').value = preset ? '60' : String(Math.round(value));
    $('#customExpiryWrap').classList.toggle('d-none', preset);
    updateExpiryHint();
  }

  function getExpiryMinutes() {
    var checked = document.querySelector('input[name="expiry"]:checked');
    if (!checked) return null;
    if (checked.value === 'custom') {
      var value = Number($('#customMinutes').value);
      if (!isFinite(value) || value < 1 || value > MAX_CUSTOM_MINUTES) return null;
      return Math.floor(value);
    }
    var preset = Number(checked.value);
    return isFinite(preset) && preset > 0 ? preset : null;
  }

  function updateExpiryHint() {
    var hint = $('#expiryHint');
    var input = $('#customMinutes');
    var isCustom = $('#expiryCustom').checked;
    var minutes = getExpiryMinutes();
    input.classList.toggle('is-invalid', isCustom && minutes === null);
    if (minutes === null) {
      hint.classList.add('is-invalid');
      hint.innerHTML = '<i class="bi bi-exclamation-circle"></i> 请输入 1–' + MAX_CUSTOM_MINUTES + ' 之间的分钟数';
      return;
    }
    hint.classList.remove('is-invalid');
    var expiresAt = Date.now() + minutes * 60000;
    hint.innerHTML = '<i class="bi bi-clock"></i> 链接将于 <strong>' + escapeHtml(formatDateTime(expiresAt)) +
      '</strong> 失效 · 有效期 ' + escapeHtml(formatExpiryLength(minutes));
  }

  /* ------------------------------------------------------------------ */
  /* File selection                                                      */
  /* ------------------------------------------------------------------ */

  function addFiles(fileList) {
    if (state.uploading) {
      UI.showToast('info', '正在上传', '请等待当前上传完成后再添加文件');
      return;
    }
    var incoming = Array.prototype.slice.call(fileList || []);
    if (!incoming.length) return;
    var keys = {};
    state.files.forEach(function (file) { keys[fileKey(file)] = true; });
    var added = 0;
    var skipped = 0;
    incoming.forEach(function (file) {
      var key = fileKey(file);
      if (keys[key]) {
        skipped += 1;
        return;
      }
      keys[key] = true;
      state.files.push(file);
      added += 1;
    });
    if (added > 0) renderFileList();
    if (skipped > 0) {
      UI.showToast('info', '已跳过重复文件', skipped + ' 个文件已经在列表中');
    }
  }

  function renderFileList() {
    var wrap = $('#fileListWrap');
    var list = $('#fileList');
    if (!state.files.length) {
      wrap.classList.add('d-none');
      list.innerHTML = '';
      $('#fileCount').textContent = '0';
      $('#fileTotal').textContent = '0 B';
      $('#fileInput').value = '';
      updateUploadState();
      return;
    }
    wrap.classList.remove('d-none');
    $('#fileCount').textContent = String(state.files.length);
    $('#fileTotal').textContent = formatBytes(totalBytes());
    list.innerHTML = state.files.map(function (file, index) {
      var visual = fileVisual(file.name);
      return '<div class="file-item">' +
        '<span class="file-icon ' + visual.tint + '"><i class="bi ' + visual.icon + '"></i></span>' +
        '<div class="file-info">' +
          '<div class="file-name" title="' + escapeHtml(file.name) + '">' + escapeHtml(file.name) + '</div>' +
          '<div class="file-size">' + escapeHtml(formatBytes(file.size)) + '</div>' +
        '</div>' +
        '<button type="button" class="btn file-remove" data-remove-index="' + index + '" ' +
          'aria-label="移除 ' + escapeHtml(file.name) + '" title="移除">' +
          '<i class="bi bi-x-lg"></i></button>' +
        '</div>';
    }).join('');
    updateUploadState();
  }

  function updateUploadState() {
    var button = $('#uploadBtn');
    var hint = $('#uploadHint');
    var minutes = getExpiryMinutes();
    var total = totalBytes();
    var max = Number(state.config.maxUploadBytes) || 0;
    var message = '';
    var isError = false;

    if (state.uploading) {
      message = '正在上传，请保持页面打开';
    } else if (state.pendingSession && !state.files.length) {
      message = '有未完成的上传，请在上方选择继续或放弃';
    } else if (!state.files.length) {
      message = '选择一个文件后即可上传';
    } else if (minutes === null) {
      message = '请填写 1–' + MAX_CUSTOM_MINUTES + ' 之间的有效期分钟数';
      isError = true;
    } else if (max > 0 && total > max) {
      message = '所选文件共 ' + formatBytes(total) + '，超过 ' + formatBytes(max) + ' 上传上限';
      isError = true;
    } else {
      message = '已选 ' + state.files.length + ' 个文件，共 ' + formatBytes(total);
    }

    hint.textContent = message;
    hint.classList.toggle('is-error', isError);
    button.disabled = state.uploading || !state.files.length || minutes === null || (max > 0 && total > max);
    button.innerHTML = state.uploading
      ? '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> 正在上传…'
      : '<i class="bi bi-cloud-arrow-up"></i> 开始上传';
    $('#cancelUploadBtn').classList.toggle('d-none', !state.uploading);
  }

  /* ------------------------------------------------------------------ */
  /* Upload + result                                                     */
  /* ------------------------------------------------------------------ */

  function showProgress(show) {
    var wrap = $('#progressWrap');
    if (show) {
      wrap.classList.remove('d-none');
    } else {
      wrap.classList.add('d-none');
      wrap.classList.remove('is-interrupted');
      $('#progressBar').style.width = '0%';
      $('#progressBarWrap').setAttribute('aria-valuenow', '0');
      $('#progressPercent').textContent = '0%';
      $('#progressText').textContent = '正在上传…';
      $('#progressDetail').textContent = '准备上传…';
    }
  }

  function sessionProgress(session) {
    var loaded = 0;
    var total = 0;
    (session.files || []).forEach(function (file) {
      var length = Math.max(0, Number(file.length) || 0);
      total += length;
      loaded += Math.max(0, Math.min(length, Number(file.received) || 0));
    });
    var percent = total > 0 ? Math.min(100, Math.round((loaded / total) * 100)) : 100;
    return { loaded: loaded, total: total, percent: percent, complete: loaded >= total };
  }

  function updateUploadProgress(session, currentFile, elapsedMs) {
    var progress = sessionProgress(session);
    $('#progressBar').style.width = progress.percent + '%';
    $('#progressBarWrap').setAttribute('aria-valuenow', String(progress.percent));
    $('#progressPercent').textContent = progress.percent + '%';
    if (progress.percent >= 100) {
      $('#progressText').textContent = '上传完成，正在处理…';
      $('#progressDetail').textContent = '服务器正在保存文件并生成分享，请稍候…';
      return;
    }
    $('#progressText').textContent = currentFile ? '正在上传 ' + currentFile.name : '正在上传…';
    var detail = '已上传 ' + formatBytes(progress.loaded) + ' / ' + formatBytes(progress.total);
    if (currentFile) {
      detail += ' · 第 ' + (currentFile.index + 1) + ' / ' + session.files.length + ' 个文件';
    }
    if (elapsedMs > 500 && progress.loaded > 0) {
      detail += ' · ' + formatBytes(progress.loaded / (elapsedMs / 1000)) + '/s';
    }
    $('#progressDetail').textContent = detail;
  }

  /* ------------------------------------------------------------------ */
  /* Resumable upload state (localStorage + server session)              */
  /* ------------------------------------------------------------------ */

  function readPendingMeta() {
    try {
      var raw = window.localStorage.getItem(PENDING_KEY);
      if (!raw) return null;
      var data = JSON.parse(raw);
      if (!data || typeof data.uploadId !== 'string' || !data.uploadId ||
          !Array.isArray(data.files) || !data.files.length) {
        window.localStorage.removeItem(PENDING_KEY);
        return null;
      }
      return {
        uploadId: data.uploadId,
        createdAt: data.createdAt || '',
        files: data.files.map(function (item) {
          return {
            name: String(item && item.name ? item.name : ''),
            size: Math.max(0, Number(item && item.size) || 0),
            lastModified: Math.max(0, Number(item && item.lastModified) || 0)
          };
        })
      };
    } catch (error) {
      return null;
    }
  }

  function writePendingMeta(meta) {
    try {
      window.localStorage.setItem(PENDING_KEY, JSON.stringify(meta));
      return true;
    } catch (error) {
      UI.showToast('warning', '无法保存续传信息', '浏览器存储不可用，刷新页面后可能无法继续上传');
      return false;
    }
  }

  function clearPendingStorage() {
    try {
      window.localStorage.removeItem(PENDING_KEY);
    } catch (error) {
      /* ignore unavailable storage */
    }
  }

  function hideResumeBanner() {
    var banner = $('#resumeBanner');
    if (banner) banner.classList.add('d-none');
  }

  function refreshResumeBanner(message) {
    var session = state.pendingSession;
    var banner = $('#resumeBanner');
    if (!banner || !session) {
      hideResumeBanner();
      return;
    }
    var progress = sessionProgress(session);
    var ready = progress.percent >= 100;
    var hasLocalFiles = !!(state.resumeLocalFiles && state.resumeLocalFiles.length);
    var needsPick = !ready && !hasLocalFiles;
    $('#resumeTitle').textContent = ready
      ? '上次上传已完成，等待生成分享'
      : (needsPick ? '发现未完成的上传，请重新选择同一批文件继续' : '发现未完成的上传');
    var follow = message || '';
    if (!follow) {
      if (ready) {
        follow = '点击“继续上传”重试生成分享';
      } else if (hasLocalFiles) {
        follow = '文件仍在当前页面，可直接继续';
      } else {
        follow = '请重新选择同一批文件继续';
      }
    }
    $('#resumeText').textContent = session.files.length + ' 个文件 · ' + formatBytes(progress.total) +
      ' · 已上传 ' + progress.percent + '% · ' + follow;
    $('#resumeContinueBtn').innerHTML = ready
      ? '<i class="bi bi-check2-circle"></i> 继续上传'
      : (needsPick ? '<i class="bi bi-folder2-open"></i> 选择文件继续' : '<i class="bi bi-play-fill"></i> 继续上传');
    banner.classList.remove('d-none');
  }

  function showResumeBanner(session, meta, localFiles, message) {
    if (session) state.pendingSession = session;
    if (meta) state.pendingMeta = meta;
    if (localFiles && localFiles.length) state.resumeLocalFiles = localFiles.slice();
    refreshResumeBanner(message);
  }

  function forgetPending() {
    state.pendingSession = null;
    state.pendingMeta = null;
    state.resumeLocalFiles = null;
    state.resumePickMode = false;
    clearPendingStorage();
    hideResumeBanner();
  }

  function normalizeSession(data) {
    if (!data || !data.uploadId) {
      throw new UI.ApiError('服务器返回的上传会话无效', 0, 'bad_response');
    }
    var chunkSize = Number(data.chunkSize);
    if (!isFinite(chunkSize) || chunkSize <= 0) chunkSize = CHUNK_FALLBACK_BYTES;
    var source = Array.isArray(data.files) ? data.files : [];
    var files = source.map(function (item, position) {
      var index = Number(item && item.index);
      if (!isFinite(index) || index < 0) index = position;
      var length = Math.max(0, Number(item && item.length) || 0);
      var received = Number(item && item.received);
      if (!isFinite(received) || received < 0) received = 0;
      return {
        index: index,
        name: String(item && item.name ? item.name : ''),
        length: length,
        received: Math.min(received, length)
      };
    }).sort(function (a, b) { return a.index - b.index; });
    if (!files.length) {
      throw new UI.ApiError('上传会话中没有文件', 0, 'bad_response');
    }
    return {
      uploadId: String(data.uploadId),
      chunkSize: chunkSize,
      expiresAt: data.expiresAt || '',
      files: files
    };
  }

  async function fetchUploadSession(uploadId) {
    var data = await UI.requestJson('/api/uploads/' + encodeURIComponent(uploadId));
    return normalizeSession(data);
  }

  async function deleteUploadSession(uploadId) {
    if (!uploadId) return;
    try {
      await fetch('/api/uploads/' + encodeURIComponent(uploadId), {
        method: 'DELETE',
        credentials: 'same-origin',
        cache: 'no-store'
      });
    } catch (error) {
      /* the server expires stale sessions automatically */
    }
  }

  function savePendingMeta(session, files) {
    var meta = {
      uploadId: session.uploadId,
      files: files.map(function (file) {
        return {
          name: file.name,
          size: file.size,
          lastModified: file.lastModified || 0
        };
      }),
      createdAt: new Date().toISOString()
    };
    writePendingMeta(meta);
    state.pendingMeta = meta;
    return meta;
  }

  function createAbortError() {
    var error = new Error('上传已取消');
    error.name = 'AbortError';
    return error;
  }

  function wait(ms, signal) {
    return new Promise(function (resolve, reject) {
      if (signal && signal.aborted) {
        reject(createAbortError());
        return;
      }
      var timer = window.setTimeout(function () {
        if (signal) signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      function onAbort() {
        window.clearTimeout(timer);
        if (signal) signal.removeEventListener('abort', onAbort);
        reject(createAbortError());
      }
      if (signal) signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  async function createUploadSession(files, expiryMinutes, burn) {
    var data = await UI.requestJson('/api/uploads', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        files: files.map(function (file) {
          return { name: file.name, length: file.size, lastModified: file.lastModified || 0 };
        }),
        expiryMinutes: expiryMinutes,
        burn: !!burn,
        title: ''
      })
    });
    return normalizeSession(data);
  }

  async function putChunk(session, fileMeta, blob, signal) {
    var url = '/api/uploads/' + encodeURIComponent(session.uploadId) + '/' +
      encodeURIComponent(String(fileMeta.index)) +
      '?offset=' + encodeURIComponent(String(fileMeta.received));
    var response;
    try {
      response = await fetch(url, {
        method: 'PUT',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: {
          'Content-Type': 'application/octet-stream',
          Accept: 'application/json'
        },
        body: blob,
        signal: signal
      });
    } catch (error) {
      if (error && error.name === 'AbortError') throw error;
      throw new UI.ApiError('网络连接中断', 0, 'network');
    }
    var text = '';
    try {
      text = await response.text();
    } catch (error) {
      text = '';
    }
    var data = null;
    if (text) {
      try {
        data = JSON.parse(text);
      } catch (error) {
        data = null;
      }
    }
    if (!response.ok) {
      var message = data && (data.error || data.message) ? (data.error || data.message) : UI.statusMessage(response.status);
      var apiError = new UI.ApiError(message, response.status, data && data.code);
      if (data && data.received !== undefined && data.received !== null) {
        apiError.received = Number(data.received);
      }
      throw apiError;
    }
    if (!data || data.received === undefined || data.received === null) {
      throw new UI.ApiError('服务器返回的分片响应无效', 0, 'bad_response');
    }
    return data;
  }

  async function uploadChunkWithRetry(session, fileMeta, localFile, signal, startedAt) {
    var retries = 0;
    var resync = 0;
    while (fileMeta.received < fileMeta.length) {
      if (signal && signal.aborted) throw createAbortError();
      var offset = fileMeta.received;
      var end = Math.min(offset + session.chunkSize, fileMeta.length);
      var blob = localFile.slice(offset, end);
      try {
        var result = await putChunk(session, fileMeta, blob, signal);
        var received = Number(result.received);
        if (!isFinite(received) || received < offset || received > fileMeta.length) {
          throw new UI.ApiError('服务器返回的分片进度无效', 0, 'bad_response');
        }
        fileMeta.received = received;
        var chunkAt = window.performance && performance.now ? performance.now() : Date.now();
        updateUploadProgress(session, fileMeta, chunkAt - startedAt);
        retries = 0;
      } catch (error) {
        if (error && error.name === 'AbortError') throw error;
        if (error && error.status === 409 && error.code === 'offset_mismatch' &&
            isFinite(Number(error.received))) {
          var serverReceived = Number(error.received);
          if (serverReceived >= 0 && serverReceived <= fileMeta.length &&
              serverReceived !== fileMeta.received) {
            fileMeta.received = serverReceived;
            resync += 1;
            if (resync > 5) {
              throw new UI.ApiError('上传进度同步失败，请稍后继续', 0, 'offset_mismatch');
            }
            retries = 0;
            continue;
          }
        }
        if (error && error.status === 404) throw error;
        if (error && error.status >= 400 && error.status < 500 &&
            error.status !== 408 && error.status !== 425 && error.status !== 429 && error.status !== 409) {
          throw error;
        }
        if (retries >= MAX_CHUNK_RETRIES) {
          var interrupted = new UI.ApiError('网络不稳定，上传已中断', 0, 'interrupted');
          interrupted.interrupted = true;
          interrupted.cause = error;
          throw interrupted;
        }
        await wait(RETRY_DELAYS[retries] || 2000, signal);
        retries += 1;
      }
    }
  }

  async function uploadSessionChunks(session, localFiles, signal) {
    var startedAt = window.performance && performance.now ? performance.now() : Date.now();
    for (var i = 0; i < session.files.length; i += 1) {
      var fileMeta = session.files[i];
      if (fileMeta.received >= fileMeta.length) continue;
      var localFile = localFiles[fileMeta.index];
      if (!localFile) {
        throw new UI.ApiError('缺少文件「' + fileMeta.name + '」，请重新选择原文件', 0, 'file_missing');
      }
      if (localFile.size !== fileMeta.length) {
        throw new UI.ApiError('文件「' + fileMeta.name + '」大小与上次不一致，无法继续', 0, 'file_changed');
      }
      while (fileMeta.received < fileMeta.length) {
        var now = window.performance && performance.now ? performance.now() : Date.now();
        updateUploadProgress(session, fileMeta, now - startedAt);
        await uploadChunkWithRetry(session, fileMeta, localFile, signal, startedAt);
      }
    }
    var finishedAt = window.performance && performance.now ? performance.now() : Date.now();
    updateUploadProgress(session, null, finishedAt - startedAt);
  }

  async function completeUploadSession(session) {
    try {
      var data = await UI.requestJson('/api/uploads/' + encodeURIComponent(session.uploadId) + '/complete', {
        method: 'POST'
      });
      if (!data || !data.share) {
        throw new UI.ApiError('服务器返回的分享信息无效', 0, 'bad_response');
      }
      return data.share;
    } catch (error) {
      if (error && error.status === 409 && error.code === 'upload_incomplete') {
        var latest = await fetchUploadSession(session.uploadId);
        session.files = latest.files;
        if (sessionProgress(session).complete) {
          var retry = await UI.requestJson('/api/uploads/' + encodeURIComponent(session.uploadId) + '/complete', {
            method: 'POST'
          });
          if (retry && retry.share) return retry.share;
          throw new UI.ApiError('服务器返回的分享信息无效', 0, 'bad_response');
        }
        var interrupted = new UI.ApiError('部分文件尚未上传完成', 0, 'upload_incomplete');
        interrupted.interrupted = true;
        throw interrupted;
      }
      throw error;
    }
  }

  function matchLocalFiles(candidates, session) {
    var pool = Array.prototype.slice.call(candidates || []);
    var ordered = [];
    var missing = [];
    session.files.forEach(function (fileMeta) {
      var found = -1;
      var i;
      for (i = 0; i < pool.length; i += 1) {
        if (pool[i].size === fileMeta.length && pool[i].name === fileMeta.name) {
          found = i;
          break;
        }
      }
      if (found < 0) {
        for (i = 0; i < pool.length; i += 1) {
          if (pool[i].size === fileMeta.length) {
            found = i;
            break;
          }
        }
      }
      if (found < 0) {
        missing.push(fileMeta.name || ('第 ' + (fileMeta.index + 1) + ' 个文件'));
        return;
      }
      ordered[fileMeta.index] = pool[found];
      pool.splice(found, 1);
    });
    return { files: ordered, missing: missing };
  }

  function promptResumeSelection(session, problem) {
    state.resumePickMode = true;
    var names = session.files.map(function (fileMeta) {
      return fileMeta.name || ('第 ' + (fileMeta.index + 1) + ' 个文件');
    });
    var label = names.slice(0, 3).join('、') + (names.length > 3 ? ' 等 ' + names.length + ' 个文件' : '');
    refreshResumeBanner((problem ? problem + '，' : '') + '请重新选择原文件：' + label);
    UI.showToast('info', '请重新选择原文件', '浏览器不会保留文件内容，选择与上次相同的文件后即可继续');
    var input = $('#fileInput');
    if (input) input.click();
  }

  async function restorePendingUpload() {
    var meta = readPendingMeta();
    if (!meta) return;
    state.pendingMeta = meta;
    try {
      var session = await fetchUploadSession(meta.uploadId);
      if (state.uploading) return;
      if (state.pendingSession && state.pendingSession.uploadId !== session.uploadId) return;
      state.pendingSession = session;
      state.pendingMeta = meta;
      showResumeBanner(session, meta, null);
      updateUploadState();
    } catch (error) {
      if (error && error.status === 404) {
        clearPendingStorage();
        state.pendingMeta = null;
        UI.showToast('info', '上次的上传已失效', '未完成的上传会话已过期，请重新选择文件');
      }
    }
  }

  async function continuePendingUpload() {
    if (state.uploading) return;
    var session = state.pendingSession;
    var meta = state.pendingMeta || readPendingMeta();
    var candidates = state.resumeLocalFiles || state.files;

    if (session && !candidates.length) {
      promptResumeSelection(session);
      return;
    }

    var uploadId = session ? session.uploadId : (meta ? meta.uploadId : '');
    if (!uploadId) {
      UI.showToast('info', '没有可继续的上传', '请重新选择文件上传');
      return;
    }
    try {
      session = await fetchUploadSession(uploadId);
    } catch (error) {
      if (error && error.status === 404) {
        forgetPending();
        updateUploadState();
        UI.showToast('error', '上传会话已过期', '请重新选择文件上传');
      } else {
        UI.showToast('error', '无法读取上传进度', (error && error.message) || '请稍后重试');
      }
      return;
    }
    state.pendingSession = session;
    state.pendingMeta = meta;
    candidates = state.resumeLocalFiles || state.files;
    if (!candidates.length) {
      promptResumeSelection(session);
      return;
    }
    var match = matchLocalFiles(candidates, session);
    if (match.missing.length) {
      promptResumeSelection(session, '还缺少：' + match.missing.join('、'));
      return;
    }
    await startResumeUpload(session, match.files);
  }

  async function resumeFromSelectedFiles(picked) {
    var meta = state.pendingMeta || readPendingMeta();
    var session = state.pendingSession;
    var uploadId = session ? session.uploadId : (meta ? meta.uploadId : '');
    if (!uploadId) {
      UI.showToast('info', '没有可继续的上传', '请重新选择文件上传');
      return;
    }
    try {
      session = await fetchUploadSession(uploadId);
    } catch (error) {
      if (error && error.status === 404) {
        forgetPending();
        updateUploadState();
        UI.showToast('error', '上传会话已过期', '请重新选择文件上传');
      } else {
        UI.showToast('error', '无法读取上传进度', (error && error.message) || '请稍后重试');
      }
      return;
    }
    state.pendingSession = session;
    state.pendingMeta = meta;
    var match = matchLocalFiles(picked, session);
    if (match.missing.length) {
      UI.showToast('error', '文件不匹配', '还缺少：' + match.missing.join('、') + '，请选择与上次相同的文件');
      refreshResumeBanner('文件不匹配，请重新选择原来的文件');
      return;
    }
    await startResumeUpload(session, match.files);
  }

  async function startResumeUpload(session, files) {
    state.uploading = true;
    state.uploadAbortRequested = false;
    state.uploadController = new AbortController();
    state.files = files;
    state.resumeLocalFiles = files.slice();
    state.resumePickMode = false;
    renderFileList();
    hideResult();
    showProgress(true);
    $('#progressWrap').classList.remove('is-interrupted');
    hideResumeBanner();
    updateUploadProgress(session, null, 0);
    updateUploadState();
    try {
      await uploadSessionChunks(session, files, state.uploadController.signal);
      $('#progressText').textContent = '上传完成，正在处理…';
      $('#progressDetail').textContent = '服务器正在保存文件并生成分享，请稍候…';
      $('#progressPercent').textContent = '100%';
      $('#progressBar').style.width = '100%';
      $('#progressBarWrap').setAttribute('aria-valuenow', '100');
      var share = await completeUploadSession(session);
      finishUploadSuccess(share);
    } catch (error) {
      handleUploadFailure(error, session);
    }
  }

  async function abandonPendingUpload() {
    if (state.uploading) {
      UI.showToast('info', '正在上传', '请先取消当前上传');
      return;
    }
    var session = state.pendingSession;
    var meta = state.pendingMeta || readPendingMeta();
    var uploadId = session ? session.uploadId : (meta ? meta.uploadId : '');
    forgetPending();
    showProgress(false);
    if (uploadId) await deleteUploadSession(uploadId);
    updateUploadState();
    UI.showToast('info', '已放弃未完成的上传', '服务器上的临时文件已清理');
  }

  async function cancelUpload() {
    if (!state.uploading) {
      UI.showToast('info', '没有正在进行的上传', '可以继续添加文件');
      return;
    }
    var session = state.pendingSession;
    state.uploadAbortRequested = true;
    var controller = state.uploadController;
    if (controller) controller.abort();
    state.uploadController = null;
    state.uploading = false;
    if (session) await deleteUploadSession(session.uploadId);
    forgetPending();
    showProgress(false);
    updateUploadState();
    UI.showToast('info', '已取消上传', '服务器上的未完成文件已清理');
  }

  function finishUploadSuccess(share) {
    forgetPending();
    state.uploading = false;
    state.uploadController = null;
    state.uploadAbortRequested = false;
    state.files = [];
    renderFileList();
    updateUploadState();
    $('#progressWrap').classList.remove('is-interrupted');
    $('#progressBar').style.width = '100%';
    $('#progressPercent').textContent = '100%';
    $('#progressText').textContent = '上传完成';
    $('#progressDetail').textContent = '分享已创建';
    window.setTimeout(function () {
      if (!state.uploading) showProgress(false);
    }, 700);
    renderResult(share);
    UI.showToast('success', '分享创建成功',
      share.burnAfterDownload ? '阅后即焚分享已就绪，首次下载后自动销毁' : '复制链接发送给同事即可下载');
    loadShares();
  }

  function handleUploadFailure(error, session) {
    state.uploading = false;
    state.uploadController = null;
    updateUploadState();

    if (error && error.name === 'AbortError') {
      if (state.uploadAbortRequested) {
        state.uploadAbortRequested = false;
        return;
      }
      forgetPending();
      showProgress(false);
      UI.showToast('info', '已取消上传', '文件没有上传到服务器');
      return;
    }

    var message = (error && error.message) || '请稍后重试';
    if (!session) {
      showProgress(false);
      if (error && error.status === 404) {
        UI.showToast('error', '上传失败', '服务端暂不支持分片上传，请确认已更新到最新版本');
      } else {
        UI.showToast('error', '上传失败', message);
      }
      return;
    }

    if (error && error.status === 404) {
      forgetPending();
      showProgress(false);
      UI.showToast('error', '上传会话已失效', '请重新选择文件上传');
      return;
    }

    var retryable = !!(error && (error.interrupted || !error.status || error.status >= 500 ||
      error.status === 409 || error.status === 429));
    if (!retryable) {
      var uploadId = session.uploadId;
      forgetPending();
      showProgress(false);
      deleteUploadSession(uploadId);
      UI.showToast('error', '上传失败', message);
      return;
    }

    state.pendingSession = session;
    showResumeBanner(session, state.pendingMeta, state.resumeLocalFiles, null);
    if (error && error.interrupted) {
      $('#progressWrap').classList.remove('d-none');
      $('#progressWrap').classList.add('is-interrupted');
      $('#progressText').textContent = '上传已中断';
      $('#progressDetail').textContent = '已保留上传进度，点击“继续上传”可恢复';
      UI.showToast('warning', '上传已中断', '已保留上传进度，可稍后点击“继续上传”恢复');
      return;
    }
    var progress = sessionProgress(session);
    if (progress.complete) {
      $('#progressWrap').classList.remove('d-none');
      $('#progressWrap').classList.add('is-interrupted');
      $('#progressText').textContent = '文件已上传，生成分享失败';
      $('#progressDetail').textContent = '点击“继续上传”重试生成分享，已上传内容不会丢失';
    } else {
      showProgress(false);
      refreshResumeBanner('上次上传尚未完成');
    }
    UI.showToast('error', '上传失败', message);
  }

  async function startUpload() {
    if (state.uploading) return;
    if (state.pendingSession) {
      refreshResumeBanner('请先继续或放弃这次上传');
      UI.showToast('info', '有未完成的上传', '请先在提示条中选择“继续上传”或“放弃”');
      return;
    }
    var minutes = getExpiryMinutes();
    if (minutes === null) {
      UI.showToast('warning', '有效期无效', '请输入 1–' + MAX_CUSTOM_MINUTES + ' 之间的分钟数');
      return;
    }
    if (!state.files.length) {
      UI.showToast('warning', '还没有选择文件', '拖拽或点击选择要分享的文件');
      return;
    }
    var total = totalBytes();
    var max = Number(state.config.maxUploadBytes) || 0;
    if (max > 0 && total > max) {
      UI.showToast('error', '超过上传上限', '所选文件共 ' + formatBytes(total) + '，上限为 ' + formatBytes(max));
      return;
    }

    var files = state.files.slice();
    var burn = $('#burnSwitch').checked;
    state.uploading = true;
    state.uploadAbortRequested = false;
    state.uploadController = new AbortController();
    state.resumeLocalFiles = files.slice();
    hideResult();
    hideResumeBanner();
    showProgress(true);
    $('#progressWrap').classList.remove('is-interrupted');
    $('#progressText').textContent = '正在准备上传…';
    $('#progressPercent').textContent = '0%';
    $('#progressBar').style.width = '0%';
    $('#progressBarWrap').setAttribute('aria-valuenow', '0');
    $('#progressDetail').textContent = '共 ' + files.length + ' 个文件 · ' + formatBytes(total) + ' · 正在创建上传会话…';
    updateUploadState();

    try {
      var session = await createUploadSession(files, minutes, burn);
      state.pendingSession = session;
      savePendingMeta(session, files);
      await uploadSessionChunks(session, files, state.uploadController.signal);
      $('#progressText').textContent = '上传完成，正在处理…';
      $('#progressDetail').textContent = '服务器正在保存文件并生成分享，请稍候…';
      $('#progressPercent').textContent = '100%';
      $('#progressBar').style.width = '100%';
      $('#progressBarWrap').setAttribute('aria-valuenow', '100');
      var share = await completeUploadSession(session);
      finishUploadSuccess(share);
    } catch (error) {
      handleUploadFailure(error, state.pendingSession);
    }
  }

  function hideResult() {
    $('#resultCard').classList.add('d-none');
  }

  function renderResult(share) {
    state.lastResult = share;
    var url = absoluteUrl('/s/' + encodeURIComponent(share.id));
    var files = Array.isArray(share.files) ? share.files : [];

    var urlText = $('#resultInternalUrl');
    urlText.textContent = url;
    urlText.title = url;
    var urlCopy = $('#resultInternalCopy');
    urlCopy.dataset.copyUrl = url;
    urlCopy.dataset.copyLabel = '分享链接';
    $('#resultInternalOpen').href = url;

    var bits = [formatBytes(share.totalBytes || 0)];
    bits.push(files.length > 1 ? files.length + ' 个文件' : '单个文件');
    if (share.isPackaged) bits.push('打包为 .7z');
    $('#resultMeta').textContent = bits.join(' · ');

    $('#resultBurnAlert').classList.toggle('d-none', !share.burnAfterDownload);

    var expires = parseTimestamp(share.expiresAt);
    var created = parseTimestamp(share.createdAt);
    var text = '';
    if (expires !== null) {
      text = '有效期至 ' + formatDateTime(expires);
      if (created !== null && expires > created) {
        text += '（' + formatExpiryLength(Math.round((expires - created) / 60000)) + '）';
      }
    }
    $('#resultExpiryText').textContent = text;

    var card = $('#resultCard');
    card.classList.remove('d-none');
    card.focus({ preventScroll: true });
    card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  /* ------------------------------------------------------------------ */
  /* My shares                                                           */
  /* ------------------------------------------------------------------ */

  async function loadShares(options) {
    var opts = options || {};
    if (state.sharesLoading) return;
    state.sharesLoading = true;
    var loading = $('#sharesLoading');
    var errorBox = $('#sharesError');
    var empty = $('#sharesEmpty');
    var refreshBtn = $('#refreshSharesBtn');

    errorBox.classList.add('d-none');
    refreshBtn.classList.add('is-loading');
    if (!state.shares.length) {
      loading.classList.remove('d-none');
      empty.classList.add('d-none');
      $('#sharesList').innerHTML = '';
    }

    try {
      var data = await UI.requestJson('/api/shares');
      state.shares = Array.isArray(data && data.shares) ? data.shares : [];
      state.sharesLoadedAt = Date.now();
      renderShares();
    } catch (error) {
      if (!opts.silent || !state.shares.length) showSharesError(error);
    } finally {
      state.sharesLoading = false;
      loading.classList.add('d-none');
      refreshBtn.classList.remove('is-loading');
    }
  }

  function showSharesError(error) {
    var box = $('#sharesError');
    $('#sharesErrorText').textContent = (error && error.message) || '无法加载我的分享';
    box.classList.remove('d-none');
    $('#sharesEmpty').classList.add('d-none');
    $('#sharesLoading').classList.add('d-none');
    if (!state.shares.length) $('#sharesList').innerHTML = '';
  }

  function renderShares() {
    $('#sharesLoading').classList.add('d-none');
    $('#sharesError').classList.add('d-none');
    $('#shareCountBadge').textContent = state.shares.length + ' 个';
    if (!state.shares.length) {
      $('#sharesEmpty').classList.remove('d-none');
      $('#sharesList').innerHTML = '';
      return;
    }
    $('#sharesEmpty').classList.add('d-none');
    var sorted = state.shares.slice().sort(function (a, b) {
      return (parseTimestamp(b.createdAt) || 0) - (parseTimestamp(a.createdAt) || 0);
    });
    $('#sharesList').innerHTML = sorted.map(shareRowHtml).join('');
    tickCountdowns();
  }

  function shareRowHtml(share) {
    var id = String(share.id || '');
    var copyUrl = absoluteUrl('/s/' + encodeURIComponent(id));
    var expires = parseTimestamp(share.expiresAt) || 0;
    var remaining = expires - Date.now();
    var expired = !!share.isRevoked || !!share.isExpired || remaining <= 0;
    var soon = !expired && remaining <= 10 * 60 * 1000;
    var statusClass = expired ? 'expired' : soon ? 'expiring' : 'ok';
    var statusText = expired ? (share.isRevoked ? '已撤销' : '已过期') : soon ? '即将过期' : '有效';
    var visual = fileVisual(share.displayName || '');
    var files = Array.isArray(share.files) ? share.files : [];
    var sourceLabel = share.source === 'desktop' ? '桌面端' : share.source === 'web' ? '网页' : (share.source || '未知');
    var sourceIcon = share.source === 'desktop' ? 'bi-pc-display' : 'bi-globe2';
    var copyLabel = '分享链接';
    var remainingText = expired ? '已过期' : '剩余 ' + formatRemaining(remaining);

    var badges = '<span class="mini-badge status ' + statusClass + '" data-status-badge>' + escapeHtml(statusText) + '</span>';
    if (share.burnAfterDownload) {
      badges += '<span class="mini-badge burn"><i class="bi bi-fire"></i> 阅后即焚</span>';
    }

    var meta = '<span class="share-meta-item"><i class="bi bi-hdd"></i> ' + escapeHtml(formatBytes(share.totalBytes || 0)) + '</span>' +
      '<span class="share-meta-item"><i class="bi bi-files"></i> ' + escapeHtml(String(files.length || 1)) + ' 个文件</span>' +
      '<span class="share-meta-item countdown' + (expired ? ' is-expired' : '') + '" data-expires="' + escapeHtml(share.expiresAt || '') + '"' +
        ' title="' + escapeHtml(expires ? '有效期至 ' + formatDateTime(expires) : '') + '">' +
        '<i class="bi bi-hourglass-split"></i> <span class="countdown-value">' + escapeHtml(remainingText) + '</span></span>' +
      '<span class="share-meta-item"><i class="bi bi-download"></i> 已下载 ' + escapeHtml(String(share.downloadCount || 0)) + ' 次</span>' +
      '<span class="share-meta-item"><i class="bi ' + sourceIcon + '"></i> ' + escapeHtml(sourceLabel) + '</span>' +
      '<span class="share-meta-item"><i class="bi bi-clock"></i> ' + escapeHtml(createdLabel(share.createdAt)) + '</span>' +
      (share.isPackaged ? '<span class="share-meta-item"><i class="bi bi-file-zip"></i> 打包文件</span>' : '');

    var actions = '';
    if (!expired) {
      actions += '<button type="button" class="btn btn-sm btn-outline-primary" data-copy-url="' + escapeHtml(copyUrl) + '"' +
        ' data-copy-label="' + escapeHtml(copyLabel) + '" title="复制链接：' + escapeHtml(copyUrl) + '">' +
        '<i class="bi bi-clipboard"></i><span class="d-none d-md-inline"> 复制</span></button>';
    }
    actions += '<a class="btn btn-sm btn-outline-secondary" href="' + escapeHtml('/s/' + encodeURIComponent(id)) + '"' +
      ' target="_blank" rel="noopener" title="查看下载页"><i class="bi bi-box-arrow-up-right"></i>' +
      '<span class="d-none d-md-inline"> 查看</span></a>';
    if (!expired) {
      actions += '<button type="button" class="btn btn-sm btn-outline-danger" data-revoke-id="' + escapeHtml(id) + '"' +
        ' title="终止分享并删除文件"><i class="bi bi-x-circle"></i><span class="d-none d-md-inline"> 终止</span></button>';
    }

    return '<div class="share-row' + (expired ? ' is-expired' : '') + '" data-share-id="' + escapeHtml(id) + '">' +
      '<span class="share-icon ' + visual.tint + '"><i class="bi ' + visual.icon + '"></i></span>' +
      '<div class="share-main">' +
        '<div class="share-title-row">' +
          '<span class="share-name" title="' + escapeHtml(share.displayName || '') + '">' +
            escapeHtml(share.displayName || '未命名分享') + '</span>' + badges +
        '</div>' +
        '<div class="share-meta">' + meta + '</div>' +
      '</div>' +
      '<div class="share-actions">' + actions + '</div>' +
    '</div>';
  }

  function tickCountdowns() {
    var now = Date.now();
    var changed = false;
    $$('#sharesList .countdown[data-expires]').forEach(function (element) {
      var expires = parseTimestamp(element.getAttribute('data-expires'));
      if (expires === null) return;
      var remaining = expires - now;
      var value = element.querySelector('.countdown-value');
      if (value) value.textContent = remaining > 0 ? '剩余 ' + formatRemaining(remaining) : '已过期';
      element.classList.toggle('is-expired', remaining <= 0);
      if (remaining <= 0) {
        var row = element.closest('.share-row');
        if (row && !row.classList.contains('is-expired')) {
          row.classList.add('is-expired');
          var badge = row.querySelector('[data-status-badge]');
          if (badge) {
            badge.textContent = '已过期';
            badge.className = 'mini-badge status expired';
          }
          var revoke = row.querySelector('[data-revoke-id]');
          if (revoke) revoke.remove();
          Array.prototype.forEach.call(row.querySelectorAll('[data-copy-url]'), function (button) {
            button.classList.add('d-none');
          });
          changed = true;
        }
      }
    });
    if (changed && !state.expiredReloadScheduled) {
      state.expiredReloadScheduled = true;
      window.setTimeout(function () {
        state.expiredReloadScheduled = false;
        loadShares({ silent: true });
      }, 4000);
    }
  }

  function resetRevokeButton() {
    var button = $('#revokeConfirmBtn');
    button.disabled = false;
    button.innerHTML = '<i class="bi bi-trash3"></i> 终止分享';
  }

  function openRevokeModal(id) {
    var share = state.shares.find(function (item) { return String(item.id) === String(id); });
    if (!share) return;
    state.revokeTarget = share;
    $('#revokeShareName').textContent = share.displayName || '未命名分享';
    $('#revokeShareMeta').textContent = formatBytes(share.totalBytes || 0) + ' · 已下载 ' + (share.downloadCount || 0) + ' 次';
    resetRevokeButton();
    if (window.bootstrap && window.bootstrap.Modal) {
      if (!state.revokeModal) {
        state.revokeModal = new window.bootstrap.Modal(document.getElementById('revokeModal'));
      }
      state.revokeModal.show();
      return;
    }
    var confirmed = window.confirm('确定终止分享「' + (share.displayName || '未命名分享') + '」？文件将被删除，链接立即失效。');
    if (confirmed) performRevoke();
  }

  async function performRevoke() {
    var share = state.revokeTarget;
    if (!share) return;
    var button = $('#revokeConfirmBtn');
    button.disabled = true;
    button.innerHTML = '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> 正在终止…';
    try {
      await UI.requestJson('/api/shares/' + encodeURIComponent(share.id) + '/revoke', { method: 'POST' });
      if (state.revokeModal) state.revokeModal.hide();
      UI.showToast('success', '已终止分享', '「' + (share.displayName || '未命名分享') + '」的链接已失效');
      state.revokeTarget = null;
      if (state.lastResult && state.lastResult.id === share.id) {
        hideResult();
        state.lastResult = null;
      }
      await loadShares();
    } catch (error) {
      UI.showToast('error', '终止失败', (error && error.message) || '请稍后重试');
      if (error && (error.status === 404 || error.status === 410)) {
        if (state.revokeModal) state.revokeModal.hide();
        state.revokeTarget = null;
        await loadShares();
      }
    } finally {
      resetRevokeButton();
    }
  }

  /* ------------------------------------------------------------------ */
  /* Health                                                              */
  /* ------------------------------------------------------------------ */

  async function refreshHealth() {
    try {
      var health = await UI.requestJson('/api/health');
      setStatus('ok', '服务正常', '已运行 ' + formatUptime(health && health.uptimeSeconds));
      if (health && health.version) setVersion(health.version);
    } catch (error) {
      setStatus('error', '服务未连接', '无法连接后端服务');
    }
  }

  /* ------------------------------------------------------------------ */
  /* Events                                                              */
  /* ------------------------------------------------------------------ */

  function bindEvents() {
    var dropzone = $('#dropzone');
    var fileInput = $('#fileInput');
    var dragDepth = 0;

    dropzone.addEventListener('click', function (event) {
      if (event.target === fileInput) return;
      if (state.uploading) {
        UI.showToast('info', '正在上传', '请等待当前上传完成');
        return;
      }
      if (state.resumePickMode) state.resumePickMode = false;
      fileInput.click();
    });

    dropzone.addEventListener('keydown', function (event) {
      if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
        event.preventDefault();
        dropzone.click();
      }
    });

    fileInput.addEventListener('change', function () {
      var files = Array.prototype.slice.call(fileInput.files || []);
      fileInput.value = '';
      if (state.resumePickMode) {
        state.resumePickMode = false;
        if (files.length) resumeFromSelectedFiles(files);
        return;
      }
      addFiles(files);
    });
    fileInput.addEventListener('cancel', function () {
      state.resumePickMode = false;
    });

    dropzone.addEventListener('dragenter', function (event) {
      event.preventDefault();
      if (state.uploading) return;
      dragDepth += 1;
      dropzone.classList.add('dragover');
    });
    dropzone.addEventListener('dragover', function (event) {
      event.preventDefault();
      if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
    });
    dropzone.addEventListener('dragleave', function () {
      dragDepth = Math.max(0, dragDepth - 1);
      if (dragDepth === 0) dropzone.classList.remove('dragover');
    });
    dropzone.addEventListener('drop', function (event) {
      event.preventDefault();
      dragDepth = 0;
      dropzone.classList.remove('dragover');
      if (state.uploading) {
        UI.showToast('info', '正在上传', '请等待当前上传完成');
        return;
      }
      if (event.dataTransfer && event.dataTransfer.files) addFiles(event.dataTransfer.files);
    });
    window.addEventListener('dragover', function (event) { event.preventDefault(); });
    window.addEventListener('drop', function (event) { event.preventDefault(); });

    $('#fileList').addEventListener('click', function (event) {
      var button = event.target.closest('[data-remove-index]');
      if (!button) return;
      if (state.uploading) {
        UI.showToast('info', '正在上传', '上传过程中不能修改文件列表');
        return;
      }
      var index = Number(button.getAttribute('data-remove-index'));
      if (isFinite(index) && index >= 0 && index < state.files.length) {
        state.files.splice(index, 1);
        renderFileList();
      }
    });

    $('#clearFilesBtn').addEventListener('click', function () {
      if (state.uploading) {
        UI.showToast('info', '正在上传', '上传过程中不能修改文件列表');
        return;
      }
      if (!state.files.length) return;
      state.files = [];
      renderFileList();
      UI.showToast('info', '已清空', '文件列表已清空');
    });

    $$('input[name="expiry"]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        state.expiryTouched = true;
        var isCustom = radio.value === 'custom' && radio.checked;
        $('#customExpiryWrap').classList.toggle('d-none', !isCustom);
        if (isCustom) {
          var input = $('#customMinutes');
          input.focus();
          input.select();
        }
        updateExpiryHint();
        updateUploadState();
      });
    });

    $('#customMinutes').addEventListener('input', function () {
      state.expiryTouched = true;
      updateExpiryHint();
      updateUploadState();
    });

    $('#burnSwitch').addEventListener('change', function () {
      state.burnTouched = true;
    });

    $('#uploadBtn').addEventListener('click', startUpload);
    $('#cancelUploadBtn').addEventListener('click', cancelUpload);
    $('#resumeContinueBtn').addEventListener('click', continuePendingUpload);
    $('#resumeDiscardBtn').addEventListener('click', abandonPendingUpload);
    $('#newShareBtn').addEventListener('click', function () {
      hideResult();
      $('#dropzone').scrollIntoView({ behavior: 'smooth', block: 'center' });
      window.setTimeout(function () { $('#dropzone').focus({ preventScroll: true }); }, 350);
    });

    $('#refreshSharesBtn').addEventListener('click', function () { loadShares(); });
    $('#sharesRetryBtn').addEventListener('click', function () { loadShares(); });

    $('#sharesList').addEventListener('click', function (event) {
      var revokeButton = event.target.closest('[data-revoke-id]');
      if (revokeButton) {
        openRevokeModal(revokeButton.getAttribute('data-revoke-id'));
      }
    });

    $('#revokeConfirmBtn').addEventListener('click', performRevoke);
    var modal = document.getElementById('revokeModal');
    if (modal) {
      modal.addEventListener('hidden.bs.modal', function () {
        state.revokeTarget = null;
        resetRevokeButton();
      });
    }

    window.addEventListener('beforeunload', function (event) {
      if (!state.uploading) return;
      event.preventDefault();
      event.returnValue = '文件正在上传，离开页面会中断上传。';
      return event.returnValue;
    });
  }

  /* ------------------------------------------------------------------ */
  /* Boot                                                                */
  /* ------------------------------------------------------------------ */

  function init() {
    bindEvents();
    updateExpiryHint();
    updateUploadState();
    restorePendingUpload();
    loadConfig();
    refreshHealth();
    loadShares();

    window.setInterval(tickCountdowns, 1000);
    window.setInterval(function () {
      if (document.visibilityState === 'visible') refreshHealth();
    }, 30000);
    window.setInterval(function () {
      if (document.visibilityState !== 'visible' || state.uploading || state.sharesLoading) return;
      loadShares({ silent: true });
    }, 60000);
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState !== 'visible') return;
      refreshHealth();
      if (Date.now() - state.sharesLoadedAt > 30000) loadShares({ silent: true });
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
