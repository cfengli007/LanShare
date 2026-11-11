/* LanShare web UI shared helpers — no external dependencies. */
(function (global) {
  'use strict';

  var $ = function (selector, root) {
    return (root || document).querySelector(selector);
  };

  var $$ = function (selector, root) {
    return Array.prototype.slice.call((root || document).querySelectorAll(selector));
  };

  var ESCAPE_MAP = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

  function escapeHtml(value) {
    return String(value === null || value === undefined ? '' : value).replace(/[&<>"']/g, function (ch) {
      return ESCAPE_MAP[ch];
    });
  }

  function parseTimestamp(value) {
    if (value === null || value === undefined || value === '') return null;
    var time = typeof value === 'number' ? value : Date.parse(value);
    return typeof time === 'number' && isFinite(time) ? time : null;
  }

  function formatBytes(bytes) {
    var value = Number(bytes);
    if (!isFinite(value) || value <= 0) return '0 B';
    var units = ['B', 'KB', 'MB', 'GB', 'TB'];
    var index = 0;
    while (value >= 1024 && index < units.length - 1) {
      value = value / 1024;
      index += 1;
    }
    var rounded = value >= 100 ? Math.round(value) : Math.round(value * 10) / 10;
    return rounded + ' ' + units[index];
  }

  function formatDateTime(value) {
    var time = parseTimestamp(value);
    if (time === null) return '—';
    var date = new Date(time);
    var options = { month: 'long', day: 'numeric', hour: '2-digit', minute: '2-digit' };
    if (date.getFullYear() !== new Date().getFullYear()) options.year = 'numeric';
    try {
      return date.toLocaleString('zh-CN', options);
    } catch (error) {
      return date.toLocaleString();
    }
  }

  function formatRemaining(ms) {
    if (!isFinite(ms) || ms <= 0) return '已过期';
    var total = Math.floor(ms / 1000);
    var days = Math.floor(total / 86400);
    var hours = Math.floor((total % 86400) / 3600);
    var minutes = Math.floor((total % 3600) / 60);
    var seconds = total % 60;
    var pad = function (n) { return String(n).padStart(2, '0'); };
    if (days > 0) return days + ' 天 ' + pad(hours) + ':' + pad(minutes) + ':' + pad(seconds);
    return pad(hours) + ':' + pad(minutes) + ':' + pad(seconds);
  }

  function formatExpiryLength(minutes) {
    var total = Math.max(1, Math.round(Number(minutes) || 0));
    if (total % 1440 === 0 && total >= 2880) return (total / 1440) + ' 天';
    if (total % 60 === 0 && total < 1440) return (total / 60) + ' 小时';
    var days = Math.floor(total / 1440);
    var hours = Math.floor((total % 1440) / 60);
    var mins = total % 60;
    var parts = [];
    if (days) parts.push(days + ' 天');
    if (hours) parts.push(hours + ' 小时');
    if (mins) parts.push(mins + ' 分钟');
    return parts.join(' ');
  }

  function formatRelative(value) {
    var time = parseTimestamp(value);
    if (time === null) return '—';
    var diff = Date.now() - time;
    if (diff < 60000) return '刚刚';
    if (diff < 3600000) return Math.floor(diff / 60000) + ' 分钟前';
    if (diff < 86400000) return Math.floor(diff / 3600000) + ' 小时前';
    if (diff < 604800000) return Math.floor(diff / 86400000) + ' 天前';
    return formatDateTime(time);
  }

  var FILE_TYPES = [
    { exts: ['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'svg', 'heic', 'avif'], icon: 'bi-file-earmark-image', tint: 'tint-image' },
    { exts: ['mp4', 'mkv', 'mov', 'avi', 'webm', 'wmv', 'flv', 'm4v'], icon: 'bi-file-earmark-play', tint: 'tint-video' },
    { exts: ['mp3', 'wav', 'flac', 'aac', 'ogg', 'm4a', 'wma'], icon: 'bi-file-earmark-music', tint: 'tint-audio' },
    { exts: ['zip', '7z', 'rar', 'tar', 'gz', 'bz2', 'xz'], icon: 'bi-file-earmark-zip', tint: 'tint-archive' },
    { exts: ['pdf'], icon: 'bi-file-earmark-pdf', tint: 'tint-pdf' },
    { exts: ['doc', 'docx', 'rtf'], icon: 'bi-file-earmark-word', tint: 'tint-doc' },
    { exts: ['xls', 'xlsx', 'csv'], icon: 'bi-file-earmark-excel', tint: 'tint-sheet' },
    { exts: ['ppt', 'pptx'], icon: 'bi-file-earmark-ppt', tint: 'tint-slide' },
    { exts: ['txt', 'md', 'log'], icon: 'bi-file-earmark-text', tint: 'tint-text' },
    { exts: ['js', 'ts', 'json', 'html', 'css', 'cs', 'py', 'java', 'go', 'rs', 'sh', 'ps1', 'xml', 'yml', 'yaml'], icon: 'bi-file-earmark-code', tint: 'tint-code' },
    { exts: ['exe', 'msi', 'dll', 'apk', 'iso', 'img'], icon: 'bi-file-earmark-binary', tint: 'tint-binary' }
  ];

  function fileVisual(name) {
    var text = String(name || '');
    var index = text.lastIndexOf('.');
    var ext = index >= 0 ? text.slice(index + 1).toLowerCase() : '';
    for (var i = 0; i < FILE_TYPES.length; i += 1) {
      if (FILE_TYPES[i].exts.indexOf(ext) >= 0) return FILE_TYPES[i];
    }
    return { icon: 'bi-file-earmark', tint: 'tint-default' };
  }

  function ApiError(message, status, code) {
    Error.call(this, message);
    this.name = 'ApiError';
    this.message = message;
    this.status = status || 0;
    this.code = code || '';
  }
  ApiError.prototype = Object.create(Error.prototype);
  ApiError.prototype.constructor = ApiError;

  function statusMessage(status) {
    switch (status) {
      case 400: return '请求参数有误';
      case 403: return '没有权限执行该操作';
      case 404: return '请求的资源不存在';
      case 410: return '分享已失效';
      case 413: return '文件超过上传上限';
      case 500: return '服务端出错了，请稍后重试';
      default: return '请求失败（HTTP ' + status + '）';
    }
  }

  async function requestJson(path, options) {
    var opts = options || {};
    var response;
    try {
      response = await fetch(path, {
        method: opts.method || 'GET',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: Object.assign({ Accept: 'application/json' }, opts.headers || {}),
        body: opts.body
      });
    } catch (error) {
      throw new ApiError('无法连接到服务，请确认服务正在运行', 0, 'network');
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
      var message = data && (data.error || data.message) ? (data.error || data.message) : statusMessage(response.status);
      throw new ApiError(message, response.status, data && data.code);
    }
    return data;
  }

  var TOAST_META = {
    success: { icon: 'bi-check-circle-fill', cls: 'toast-success', delay: 3800 },
    error: { icon: 'bi-x-circle-fill', cls: 'toast-error', delay: 7000 },
    warning: { icon: 'bi-exclamation-triangle-fill', cls: 'toast-warning', delay: 6000 },
    info: { icon: 'bi-info-circle-fill', cls: 'toast-info', delay: 4200 }
  };

  function showToast(type, title, message) {
    var container = document.getElementById('toastContainer');
    if (!container) return;
    var meta = TOAST_META[type] || TOAST_META.info;
    var node = document.createElement('div');
    node.className = 'toast ls-toast ' + meta.cls;
    node.setAttribute('role', type === 'error' ? 'alert' : 'status');
    node.setAttribute('aria-live', type === 'error' ? 'assertive' : 'polite');
    node.setAttribute('aria-atomic', 'true');
    node.innerHTML =
      '<div class="d-flex align-items-start">' +
        '<div class="toast-body d-flex align-items-start gap-2">' +
          '<i class="bi ' + meta.icon + ' toast-icon" aria-hidden="true"></i>' +
          '<div class="min-w-0">' +
            '<div class="toast-title">' + escapeHtml(title) + '</div>' +
            (message ? '<div class="toast-message">' + escapeHtml(message) + '</div>' : '') +
          '</div>' +
        '</div>' +
        '<button type="button" class="btn-close me-2 mt-2" data-bs-dismiss="toast" aria-label="关闭"></button>' +
      '</div>';
    while (container.children.length >= 3) {
      container.removeChild(container.firstElementChild);
    }
    container.appendChild(node);
    if (global.bootstrap && global.bootstrap.Toast) {
      var toast = new global.bootstrap.Toast(node, { delay: meta.delay, autohide: true });
      node.addEventListener('hidden.bs.toast', function () { node.remove(); });
      toast.show();
    } else {
      window.setTimeout(function () { node.remove(); }, meta.delay);
    }
  }

  function legacyCopy(text) {
    var area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.top = '-1000px';
    area.style.left = '-1000px';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.focus();
    area.select();
    area.setSelectionRange(0, area.value.length);
    var ok = false;
    try {
      ok = document.execCommand('copy');
    } finally {
      area.remove();
    }
    if (!ok) throw new Error('copy command failed');
  }

  async function copyText(text, label) {
    if (!text) return false;
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
      } else {
        legacyCopy(text);
      }
      showToast('success', '已复制', (label || '链接') + '已复制到剪贴板');
      return true;
    } catch (error) {
      try {
        legacyCopy(text);
        showToast('success', '已复制', (label || '链接') + '已复制到剪贴板');
        return true;
      } catch (fallbackError) {
        showToast('error', '复制失败', '请手动复制链接');
        window.prompt('请手动复制链接：', text);
        return false;
      }
    }
  }

  document.addEventListener('click', function (event) {
    var target = event.target;
    if (!target || typeof target.closest !== 'function') return;
    var trigger = target.closest('[data-copy-url]');
    if (!trigger || trigger.disabled) return;
    var url = trigger.getAttribute('data-copy-url');
    if (!url) return;
    event.preventDefault();
    copyText(url, trigger.getAttribute('data-copy-label') || '链接');
  });

  global.LSUI = {
    $: $,
    $$: $$,
    escapeHtml: escapeHtml,
    parseTimestamp: parseTimestamp,
    formatBytes: formatBytes,
    formatDateTime: formatDateTime,
    formatRemaining: formatRemaining,
    formatExpiryLength: formatExpiryLength,
    formatRelative: formatRelative,
    fileVisual: fileVisual,
    ApiError: ApiError,
    statusMessage: statusMessage,
    requestJson: requestJson,
    showToast: showToast,
    copyText: copyText
  };
})(window);
