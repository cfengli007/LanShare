import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
const root = path.resolve(import.meta.dirname, '../src/LanShare.Server/wwwroot');
const types = { '.html':'text/html; charset=utf-8', '.css':'text/css; charset=utf-8', '.js':'text/javascript; charset=utf-8', '.woff':'font/woff', '.woff2':'font/woff2', '.ico':'image/x-icon', '.png':'image/png', '.svg':'image/svg+xml' };
http.createServer((req,res)=>{
  let p = decodeURIComponent(new URL(req.url,'http://x').pathname);
  if (p === '/' || p === '') p = '/index.html';
  if (p.startsWith('/s/')) p = '/share.html';
  const file = path.join(root, p);
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end('not found'); return; }
  res.writeHead(200, {'Content-Type': types[path.extname(file)] || 'application/octet-stream', 'Cache-Control':'no-store'});
  fs.createReadStream(file).pipe(res);
}).listen(8899, '127.0.0.1', ()=>console.log('preview http://127.0.0.1:8899'));
