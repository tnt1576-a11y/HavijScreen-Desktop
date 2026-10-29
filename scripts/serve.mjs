import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../app/', import.meta.url));
const types = { '.html': 'text/html', '.css': 'text/css', '.mjs': 'text/javascript' };
const server = http.createServer(async (req, res) => {
  try {
    const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    const path = resolve(root, pathname === '/' ? 'index.html' : '.' + pathname);
    if (!path.startsWith(root) || !types[extname(path)]) { res.writeHead(404).end(); return; }
    const data = await readFile(path);
    res.writeHead(200, { 'Content-Type': types[extname(path)] + '; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer' }); res.end(data);
  } catch { res.writeHead(404).end(); }
});
server.listen(Number(process.env.PORT || 4317), '127.0.0.1', () => console.log(`Drift development preview: http://127.0.0.1:${server.address().port}`));
