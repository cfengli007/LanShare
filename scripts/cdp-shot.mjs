import http from 'node:http';
import fs from 'node:fs';
const port = Number(process.env.CDP_PORT || 9223);
const pageUrl = process.env.PAGE_URL || 'http://127.0.0.1:8899/';
const width = Number(process.env.VIEW_WIDTH || 430);
const height = Number(process.env.VIEW_HEIGHT || 1200);
const out = process.env.OUT || 'work/shots/cdp.png';
function getJson(path) { return new Promise((resolve,reject)=>{ http.get({host:'127.0.0.1',port,path},res=>{let d='';res.on('data',c=>d+=c);res.on('end',()=>{try{resolve(JSON.parse(d));}catch(e){reject(e);}});}).on('error',reject); }); }
for (let i=0;i<40;i++){ try{ await getJson('/json/version'); break; } catch { await new Promise(r=>setTimeout(r,250)); } }
const targets=await getJson('/json/list'); const target=targets.find(t=>t.type==='page');
if(!target) throw new Error('no page');
const ws=new WebSocket(target.webSocketDebuggerUrl); let id=0; const pending=new Map(); const logs=[];
ws.onmessage=ev=>{const m=JSON.parse(ev.data); if(m.id&&pending.has(m.id)){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);} else if(m.method==='Runtime.exceptionThrown'){logs.push('EXCEPTION '+JSON.stringify(m.params.exceptionDetails?.text||m.params));} else if(m.method==='Log.entryAdded'){logs.push('LOG '+m.params.entry.level+' '+m.params.entry.text);} else if(m.method==='Runtime.consoleAPICalled'){logs.push('CONSOLE '+m.params.type+' '+m.params.args.map(a=>a.value??a.description).join(' '));} };
await new Promise((res,rej)=>{ws.onopen=res;ws.onerror=rej;});
function call(method,params={}){return new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});}
await call('Page.enable'); await call('Runtime.enable'); await call('Log.enable');
await call('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:pageUrl});
await new Promise(r=>setTimeout(r,2500));
const dims=await call('Runtime.evaluate',{expression:'JSON.stringify({vw:document.documentElement.clientWidth,sw:document.documentElement.scrollWidth,sh:Math.max(document.body.scrollHeight,document.documentElement.scrollHeight)})',returnByValue:true});
const d=JSON.parse(dims.result.value);
await call('Emulation.setDeviceMetricsOverride',{width,height:Math.max(height,d.sh),deviceScaleFactor:1,mobile:false});
await new Promise(r=>setTimeout(r,300));
const shot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,fromSurface:true});
fs.mkdirSync(out.replace(/[\\/][^\\/]+$/,''),{recursive:true});
fs.writeFileSync(out,Buffer.from(shot.data,'base64'));
console.log(JSON.stringify({url:pageUrl, ...d, out, logs},null,2));
ws.close();
