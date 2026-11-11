import http from 'node:http';
const port = Number(process.env.CDP_PORT || 9223);
const pageUrl = process.env.PAGE_URL || 'http://127.0.0.1:8899/';
const width = Number(process.env.VIEW_WIDTH || 430);
const height = Number(process.env.VIEW_HEIGHT || 1200);

function getJson(path) {
  return new Promise((resolve, reject) => {
    http.get({host:'127.0.0.1', port, path}, res => {
      let data=''; res.on('data', c=>data+=c); res.on('end', ()=>{ try{resolve(JSON.parse(data));}catch(e){reject(e);} });
    }).on('error', reject);
  });
}
for (let i=0;i<40;i++) { try { await getJson('/json/version'); break; } catch { await new Promise(r=>setTimeout(r,250)); } }
const targets = await getJson('/json/list');
const target = targets.find(t=>t.type==='page');
if (!target) throw new Error('no page target');
const ws = new WebSocket(target.webSocketDebuggerUrl);
let id=0; const pending=new Map();
ws.onmessage = ev => { const m=JSON.parse(ev.data); if (m.id && pending.has(m.id)) { const p=pending.get(m.id); pending.delete(m.id); m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result); } };
await new Promise((res,rej)=>{ ws.onopen=res; ws.onerror=rej; });
function call(method, params={}) { return new Promise((resolve,reject)=>{ const n=++id; pending.set(n,{resolve,reject}); ws.send(JSON.stringify({id:n,method,params})); }); }
await call('Page.enable');
await call('Runtime.enable');
await call('Emulation.setDeviceMetricsOverride', {width, height, deviceScaleFactor:1, mobile:false});
await call('Page.navigate', {url: pageUrl});
await new Promise(r=>setTimeout(r,1800));
const expr = "(() => {" +
  "  const vw = document.documentElement.clientWidth;" +
  "  const bad = [];" +
  "  for (const el of document.querySelectorAll('body *')) {" +
  "    const r = el.getBoundingClientRect();" +
  "    if (r.width === 0 && r.height === 0) continue;" +
  "    if (r.right > vw + 1 || r.left < -1) {" +
  "      bad.push({tag:el.tagName.toLowerCase(), cls:el.className && String(el.className).slice(0,100), id:el.id, left:Math.round(r.left), right:Math.round(r.right), width:Math.round(r.width), scrollWidth:el.scrollWidth, clientWidth:el.clientWidth});" +
  "    }" +
  "  }" +
  "  return {title:document.title, vw, bodyScrollWidth:document.body.scrollWidth, htmlScrollWidth:document.documentElement.scrollWidth, bad:bad.slice(0,40)};" +
  "})()";
const result = await call('Runtime.evaluate', {expression: expr, returnByValue:true, awaitPromise:true});
console.log(JSON.stringify(result.result.value, null, 2));
ws.close();
