#!/usr/bin/env node
// LanShare HTTP 端到端验收脚本（需要先启动 LanShare 桌面端/服务）
// 用法: node scripts/e2e.mjs [http://127.0.0.1:8910]
import { randomUUID } from 'node:crypto';

const base = (process.argv[2] || process.env.LANSHARE_BASE || 'http://127.0.0.1:8910').replace(/\/+$/, '');
let cookie = '';
const created = [];
let passed = 0;
let failed = 0;

function ok(name, condition, detail) {
  if (condition) {
    passed++;
    console.log('  PASS  ' + name);
  } else {
    failed++;
    console.log('  FAIL  ' + name + (detail === undefined ? '' : ' -> ' + JSON.stringify(detail)));
  }
}

function parseSetCookie(res) {
  const list = typeof res.headers.getSetCookie === 'function' ? res.headers.getSetCookie() : [];
  for (const raw of list) {
    const first = raw.split(';')[0];
    if (first.startsWith('lanshare_uid=')) cookie = first;
  }
}

async function request(path, options) {
  const opts = Object.assign({}, options || {});
  opts.headers = Object.assign({}, opts.headers || {});
  if (cookie && !('Cookie' in opts.headers)) opts.headers['Cookie'] = cookie;
  opts.redirect = 'manual';
  const res = await fetch(base + path, opts);
  parseSetCookie(res);
  return res;
}

async function json(res) {
  const text = await res.text();
  try { return { text, body: JSON.parse(text) }; } catch { return { text, body: null }; }
}

async function ensureCookie() {
  const res = await request('/');
  ok('GET / 返回 200 HTML', res.status === 200 && (res.headers.get('content-type') || '').includes('text/html'), res.status);
  ok('服务端下发身份 Cookie', cookie.startsWith('lanshare_uid='), cookie);
}

async function upload(files, fields) {
  const form = new FormData();
  for (const f of files) {
    form.append('files', new Blob([f.data], { type: 'application/octet-stream' }), f.name);
  }
  for (const [k, v] of Object.entries(fields || {})) form.append(k, String(v));
  return request('/api/shares', { method: 'POST', body: form });
}

async function download(id, range) {
  const headers = {};
  if (range) headers.Range = range;
  return request('/d/' + id, { headers });
}

async function main() {
  console.log('LanShare E2E: ' + base);

  const health = await request('/api/health');
  const hj = await json(health);
  ok('GET /api/health 返回 200', health.status === 200, health.status);
  ok('health.status=ok', hj.body && hj.body.status === 'ok', hj.body);
  ok('health 带 version/serverTime/uptimeSeconds', Boolean(hj.body && hj.body.version && hj.body.serverTime && typeof hj.body.uptimeSeconds === 'number'), hj.body);

  await ensureCookie();

  const cfgRes = await request('/api/config');
  const cfg = await json(cfgRes);
  ok('GET /api/config 返回 200', cfgRes.status === 200, cfgRes.status);
  ok('config 带 maxUploadBytes/lanAddresses', Boolean(cfg.body && cfg.body.maxUploadBytes > 0 && Array.isArray(cfg.body.lanAddresses)), cfg.body && cfg.body.maxUploadBytes);

  const unknown = await request('/api/definitely-not-found');
  ok('未知 /api 返回 404 JSON', unknown.status === 404 && (unknown.headers.get('content-type') || '').includes('application/json'));

  const singleName = 'E2E 单文件 ' + Date.now() + '.txt';
  const singleBody = 'hello LanShare 中文内容 ' + randomUUID();
  const singleRes = await upload([{ name: singleName, data: Buffer.from(singleBody, 'utf8') }], { expiryMinutes: 60, burn: false, title: 'E2E 单文件分享' });
  const single = await json(singleRes);
  ok('POST /api/shares 单文件返回 201', singleRes.status === 201, { status: singleRes.status, body: single.body });
  if (single.body && single.body.share) {
    const s = single.body.share;
    created.push(s.id);
    ok('单文件 isPackaged=false', s.isPackaged === false, s.isPackaged);
    ok('单文件保留中文文件名', s.files && s.files[0] && s.files[0].name === singleName, s.files);
    ok('ShareDto 带 internalUrl/externalUrl', Boolean(s.internalUrl && s.externalUrl), s);
    ok('displayName 使用 title', s.displayName === 'E2E 单文件分享', s.displayName);

    const listRes = await request('/api/shares');
    const list = await json(listRes);
    ok('GET /api/shares 包含新建分享', listRes.status === 200 && list.body && list.body.shares.some(x => x.id === s.id), listRes.status);

    const pubRes = await request('/api/public/shares/' + s.id, { headers: { Cookie: '' } });
    const pub = await json(pubRes);
    ok('公开分享接口无需 cookie', pubRes.status === 200 && pub.body && pub.body.share.id === s.id, pubRes.status);
    ok('公开分享不泄露 downloadPath', pub.body && pub.body.share && pub.body.share.downloadPath === undefined, pub.body && pub.body.share);

    const attacker = await request('/api/shares/' + s.id, { headers: { Cookie: 'lanshare_uid=' + randomUUID() } });
    ok('他人 cookie 看不到分享', attacker.status === 404, attacker.status);

    const dl = await download(s.id);
    const dlText = await dl.text();
    ok('下载返回 200', dl.status === 200, dl.status);
    ok('下载内容正确', dlText === singleBody, dlText.length);
    const cd = dl.headers.get('content-disposition') || '';
    ok('Content-Disposition 使用 RFC5987', cd.includes("filename*=UTF-8''") || cd.includes('filename*=utf-8'), cd);

    const rangeRes = await download(s.id, 'bytes=0-4');
    const rangeText = await rangeRes.text();
    ok('Range 下载返回 206', rangeRes.status === 206, rangeRes.status);
    ok('Range 内容正确', rangeText === Buffer.from(singleBody, 'utf8').subarray(0, 5).toString('utf8'), rangeText);

    const page = await request('/s/' + s.id);
    ok('分享页 /s/{id} 返回 HTML', page.status === 200 && (page.headers.get('content-type') || '').includes('text/html'), page.status);

    const revoke = await request('/api/shares/' + s.id + '/revoke', { method: 'POST' });
    const rv = await json(revoke);
    ok('终止分享返回 200/ok', revoke.status === 200 && rv.body && rv.body.ok === true, { status: revoke.status, body: rv.body });

    const after = await request('/api/shares/' + s.id);
    ok('终止后查询返回 410', after.status === 410, after.status);
    const afterDl = await download(s.id);
    ok('终止后下载返回 410', afterDl.status === 410, afterDl.status);
    const indexOf = created.indexOf(s.id);
    if (indexOf >= 0) created.splice(indexOf, 1);
  } else {
    ok('创建单文件分享有 share 字段', false, single.body);
  }

  const names = ['E2E A ' + Date.now() + '.txt', 'E2E B ' + Date.now() + '.txt'];
  const multiRes = await upload([
    { name: names[0], data: Buffer.from('aaa') },
    { name: names[1], data: Buffer.from('bbb') }
  ], { expiryMinutes: 60, burn: false });
  const multi = await json(multiRes);
  ok('POST /api/shares 多文件返回 201', multiRes.status === 201, { status: multiRes.status, body: multi.body });
  if (multi.body && multi.body.share) {
    const m = multi.body.share;
    created.push(m.id);
    ok('多文件 isPackaged=true', m.isPackaged === true, m.isPackaged);
    ok('多文件 files 保留两个文件名', m.files && m.files.length === 2, m.files);
    const dl = await download(m.id);
    const buf = Buffer.from(await dl.arrayBuffer());
    ok('打包下载 200', dl.status === 200, dl.status);
    ok('打包下载是 7z 签名', buf.length > 6 && buf[0] === 0x37 && buf[1] === 0x7a && buf[2] === 0xbc && buf[3] === 0xaf, buf.subarray(0, 8).toString('hex'));
    ok('打包 Content-Type=7z', (dl.headers.get('content-type') || '').includes('x-7z-compressed'), dl.headers.get('content-type'));
    const page = await request('/s/' + m.id);
    ok('多文件分享页 200', page.status === 200, page.status);
  } else {
    ok('创建多文件分享有 share 字段', false, multi.body);
  }

  const burnName = 'E2E burn ' + Date.now() + '.txt';
  const burnRes = await upload([{ name: burnName, data: Buffer.from('burn-once') }], { expiryMinutes: 60, burn: true });
  const burn = await json(burnRes);
  ok('创建 burn 分享 201', burnRes.status === 201, { status: burnRes.status, body: burn.body });
  if (burn.body && burn.body.share) {
    const b = burn.body.share;
    created.push(b.id);
    ok('burn 标记为 true', b.burnAfterDownload === true, b.burnAfterDownload);
    const first = await download(b.id);
    const bodyText = await first.text();
    ok('burn 首次下载成功', first.status === 200 && bodyText === 'burn-once', first.status);
    await new Promise(r => setTimeout(r, 500));
    const second = await download(b.id);
    ok('burn 二次下载返回 410', second.status === 410, second.status);
    const pub = await request('/api/public/shares/' + b.id);
    ok('burn 后公开接口 410', pub.status === 410, pub.status);
    const i = created.indexOf(b.id);
    if (i >= 0) created.splice(i, 1);
  }

  const bad = await upload([{ name: 'x.txt', data: Buffer.from('x') }], { expiryMinutes: 0 });
  ok('expiryMinutes=0 返回 400', bad.status === 400, bad.status);

  const missing = await download('does-not-exist');
  ok('未知分享下载 404', missing.status === 404, missing.status);

  const css = await request('/vendor/bootstrap/bootstrap.min.css');
  ok('本地 Bootstrap CSS 可访问', css.status === 200 && (css.headers.get('content-type') || '').includes('text/css'), css.status);

  // 清理本脚本创建但未终止的分享
  for (const id of created.slice()) {
    try { await request('/api/shares/' + id + '/revoke', { method: 'POST' }); } catch { /* ignore */ }
  }

  console.log('');
  console.log('结果: ' + passed + ' passed, ' + failed + ' failed');
  if (failed > 0) process.exitCode = 1;
}

main().catch(err => {
  console.error('E2E 异常: ' + (err && err.stack ? err.stack : err));
  process.exitCode = 2;
});
