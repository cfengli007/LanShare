/* LanShare public share page: one share, countdown and download. */
(function () {
  'use strict';

  var UI = window.LSUI;
  var $ = UI.$;
  var escapeHtml = UI.escapeHtml;
  var formatBytes = UI.formatBytes;
  var formatDateTime = UI.formatDateTime;
  var formatRemaining = UI.formatRemaining;
  var formatExpiryLength = UI.formatExpiryLength;
  var parseTimestamp = UI.parseTimestamp;
  var fileVisual = UI.fileVisual;

  var state = {
    id: '',
    share: null,
    timer: null,
    expiredHandled: false,
    config: null,
    copyUrl: ''
  };

  function parseShareId() {
    var parts = window.location.pathname.split('/').filter(function (part) { return part.length > 0; });
    var sIndex = parts.indexOf('s');
    if (sIndex >= 0 && parts[sIndex + 1]) return parts[sIndex + 1];
    if (parts.length > 1 && parts[0] === 's') return parts[1];
    var query = new URLSearchParams(window.location.search);
    return query.get('id') || '';
  }

  function showView(name) {
    ['#shareLoading', '#shareError', '#shareContent'].forEach(function (selector) {
      var element = $(selector);
      if (element) element.classList.toggle('d-none', selector !== name);
    });
  }

  function setBrand(config) {
    if (!config) return;
    var brand = $('#brandName');
    if (brand && config.productName) brand.textContent = config.productName;
    var version = $('#versionBadge');
    if (version && config.version) version.textContent = 'v' + String(config.version).replace(/^v/i, '');
    if (config.productName) document.title = '下载分享 · ' + config.productName;
  }

  function loadConfig() {
    UI.requestJson('/api/config').then(function (config) {
      state.config = config;
      setBrand(config);
    }).catch(function () {
      /* Branding is optional on the share page. */
    });
  }

  function looksExpired(error) {
    var text = (error && (error.message || error.code)) || '';
    return /过期|失效|expired/i.test(text);
  }

  function showError(error, id) {
    var title = '分享不存在';
    var message = '链接可能输入有误，或者该分享已被删除。';
    var hint = '请向分享者确认链接是否完整，或让对方重新创建分享。';
    var icon = 'bi-link-45deg';
    var status = error && error.status;

    if (status === 410) {
      title = '分享已失效';
      icon = looksExpired(error) ? 'bi-hourglass-bottom' : 'bi-slash-circle';
      message = looksExpired(error)
        ? '该分享已经过期，文件已被自动清理。'
        : '该分享已被分享者终止，文件已删除。';
      if (error && error.message) message = error.message;
      hint = '如有需要，请联系分享者重新创建。';
    } else if (status === 404) {
      title = '分享不存在';
      message = '没有找到对应的分享，链接可能不完整或已被删除。';
    } else if (status === 0) {
      title = '无法连接服务';
      icon = 'bi-wifi-off';
      message = '暂时联系不上服务器，请检查网络或确认服务是否已启动。';
      hint = '确认在同一局域网后刷新页面重试。';
    } else if (status && status >= 500) {
      title = '服务器暂时不可用';
      icon = 'bi-exclamation-octagon';
      message = '服务端处理请求时出错，请稍后重试。';
      hint = '如果问题持续出现，请联系分享者。';
    }

    $('#shareErrorTitle').textContent = title;
    $('#shareErrorText').textContent = message;
    $('#shareErrorHint').textContent = hint;
    $('#shareErrorIcon').className = 'bi ' + icon;
    var download = $('#downloadBtn');
    if (download) {
      download.removeAttribute('href');
      download.classList.add('disabled');
      download.setAttribute('aria-disabled', 'true');
      download.innerHTML = '<i class="bi bi-slash-circle"></i> 链接已失效';
    }
    var copy = $('#copyShareBtn');
    if (copy) copy.classList.add('d-none');
    var actions = $('#shareActions');
    if (actions) actions.classList.add('is-disabled');
    showView('#shareError');
    $('#sharePage').classList.add('is-error-state');
    if (id) $('#shareErrorId').textContent = id;
    $('#shareErrorIdWrap').classList.toggle('d-none', !id);
  }

  function renderShare(share) {
    state.share = share;
    var files = Array.isArray(share.files) ? share.files : [];
    var total = Number(share.totalBytes) || 0;
    var visual = fileVisual(share.displayName || (files[0] && files[0].name) || '');
    var expires = parseTimestamp(share.expiresAt);
    var created = parseTimestamp(share.createdAt);
    var invalid = !!share.isRevoked || !!share.isExpired || (expires !== null && expires <= Date.now());

    $('#shareIcon').className = 'share-hero-icon ' + visual.tint;
    $('#shareIcon').innerHTML = '<i class="bi ' + visual.icon + '"></i>';
    $('#shareDisplayName').textContent = share.displayName || '未命名分享';
    $('#shareSubtitle').textContent = (files.length > 1 ? files.length + ' 个文件' : '1 个文件') +
      ' · 共 ' + formatBytes(total) + (share.isPackaged ? ' · 打包为 .7z' : '');
    $('#shareBadge').className = 'share-badge ' + (invalid ? 'expired' : 'valid');
    $('#shareBadge').textContent = invalid ? (share.isRevoked ? '已撤销' : '已过期') : '有效分享';
    $('#shareBadge').classList.remove('d-none');

    $('#metaCreated').textContent = created !== null ? formatDateTime(created) : '未知';
    $('#metaExpires').textContent = expires !== null ? formatDateTime(expires) : '未知';
    $('#metaDownloads').textContent = '已下载 ' + (Number(share.downloadCount) || 0) + ' 次';
    $('#fileSummary').textContent = files.length + ' 个文件 · ' + formatBytes(total);
    $('#metaPackaged').classList.toggle('d-none', !share.isPackaged);
    $('#metaPackagedText').textContent = share.isPackaged
      ? '多个文件已自动打包为 .7z 文件（仅存储，不压缩），下载后解压即可。'
      : '';

    $('#burnNotice').classList.toggle('d-none', !share.burnAfterDownload);
    if (share.burnAfterDownload) {
      $('#burnNoticeText').textContent = share.downloadCount > 0
        ? '该分享设置为阅后即焚，已下载 ' + share.downloadCount + ' 次，文件可能已被销毁。'
        : '分享者设置了阅后即焚：首次完整下载后，文件将被立即删除，链接随即失效。';
    }

    renderFiles(files);
    setupActions(share, invalid);
    setupCountdown(expires);
    showView('#shareContent');
    document.title = (share.displayName || '文件下载') + ' · 内网快传';
  }

  function renderFiles(files) {
    var list = $('#shareFiles');
    if (!files.length) {
      list.innerHTML = '<div class="empty-note"><i class="bi bi-inbox"></i> 这个分享没有可下载的文件</div>';
      return;
    }
    list.innerHTML = files.map(function (file) {
      var visual = fileVisual(file.name || '');
      return '<div class="share-file">' +
        '<span class="file-icon ' + visual.tint + '"><i class="bi ' + visual.icon + '"></i></span>' +
        '<span class="share-file-name" title="' + escapeHtml(file.name || '') + '">' + escapeHtml(file.name || '未命名文件') + '</span>' +
        '<span class="share-file-size">' + escapeHtml(formatBytes(file.length || 0)) + '</span>' +
        '</div>';
    }).join('');
  }

  function setupActions(share, invalid) {
    var download = $('#downloadBtn');
    var copy = $('#copyShareBtn');
    var downloadUrl = share.downloadUrl || ('/d/' + encodeURIComponent(share.id));
    var shareUrl = window.location.origin + '/s/' + encodeURIComponent(share.id);
    state.copyUrl = shareUrl;

    download.setAttribute('href', downloadUrl);
    download.classList.toggle('disabled', invalid);
    download.setAttribute('aria-disabled', invalid ? 'true' : 'false');
    download.innerHTML = invalid
      ? '<i class="bi bi-slash-circle"></i> 链接已失效'
      : '<i class="bi bi-download"></i> 下载文件' + (share.isPackaged ? '（.7z）' : '');
    if (invalid) download.removeAttribute('href');

    copy.dataset.copyUrl = shareUrl;
    copy.dataset.copyLabel = '分享链接';
    copy.classList.toggle('d-none', invalid);

    $('#shareActions').classList.toggle('is-disabled', invalid);
    $('#statusNotice').classList.toggle('d-none', !invalid);
    if (invalid) {
      $('#statusNoticeText').textContent = share.isRevoked
        ? '该分享已被分享者终止，链接不再可用。'
        : '该分享已过期，文件已被自动清理。';
      $('#expiryBarWrap').classList.add('d-none');
    } else {
      $('#expiryBarWrap').classList.remove('d-none');
    }
  }

  function setupCountdown(expires) {
    if (state.timer) {
      window.clearInterval(state.timer);
      state.timer = null;
    }
    if (expires === null) {
      $('#countdown').textContent = '长期有效';
      $('#expiryBar').style.width = '100%';
      $('#expiryCaption').textContent = '未设置过期时间';
      return;
    }
    var created = state.share && state.share.createdAt ? parseTimestamp(state.share.createdAt) : null;
    var duration = created !== null && expires > created ? expires - created : 0;

    function tick() {
      var remaining = expires - Date.now();
      var expired = remaining <= 0;
      $('#countdown').textContent = expired ? '已过期' : formatRemaining(remaining);
      $('#countdown').classList.toggle('is-expired', expired);
      if (duration > 0) {
        var percent = Math.max(0, Math.min(100, 100 - (remaining / duration) * 100));
        $('#expiryBar').style.width = percent.toFixed(1) + '%';
      }
      if (expired && !state.expiredHandled) {
        state.expiredHandled = true;
        handleExpired();
      }
    }

    tick();
    if (!state.expiredHandled) state.timer = window.setInterval(tick, 1000);
  }

  function handleExpired() {
    if (state.timer) {
      window.clearInterval(state.timer);
      state.timer = null;
    }
    var badge = $('#shareBadge');
    if (badge) {
      badge.className = 'share-badge expired';
      badge.textContent = '已过期';
    }
    var download = $('#downloadBtn');
    if (download) {
      download.removeAttribute('href');
      download.classList.add('disabled');
      download.setAttribute('aria-disabled', 'true');
      download.innerHTML = '<i class="bi bi-slash-circle"></i> 链接已失效';
    }
    var copy = $('#copyShareBtn');
    if (copy) copy.classList.add('d-none');
    $('#statusNotice').classList.remove('d-none');
    $('#statusNoticeText').textContent = '该分享刚刚过期，链接不再可用，文件将被自动清理。';
    $('#expiryBarWrap').classList.add('d-none');
    $('#shareActions').classList.add('is-disabled');
    UI.showToast('warning', '分享已过期', '链接已失效，下载已停止');
  }

  function bindEvents() {
    $('#retryBtn').addEventListener('click', function () {
      window.location.reload();
    });
    $('#downloadBtn').addEventListener('click', function (event) {
      if (event.currentTarget.classList.contains('disabled') || !event.currentTarget.getAttribute('href')) {
        event.preventDefault();
      }
    });
    window.addEventListener('pagehide', function () {
      if (state.timer) {
        window.clearInterval(state.timer);
        state.timer = null;
      }
    });
  }

  async function init() {
    bindEvents();
    loadConfig();
    state.id = parseShareId();
    if (!state.id) {
      showError({ status: 404 });
      return;
    }
    $('#backHomeLink').setAttribute('href', '/');
    try {
      var data = await UI.requestJson('/api/public/shares/' + encodeURIComponent(state.id));
      if (!data || !data.share) throw new UI.ApiError('分享数据格式不正确', 500, 'bad_response');
      renderShare(data.share);
    } catch (error) {
      showError(error, state.id);
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
