#!/usr/bin/env node
/**
 * Builds Documentation/ARRISE/index.html - a standalone, offline viewer for the
 * ARRISE documentation set.
 *
 * Markdown stays the editable source of truth (it is what version control
 * reviews); this generator only produces a pleasant way to READ it. Every
 * document is embedded in the HTML, so the file works from disk with no server,
 * no network and no dependencies.
 *
 * Run from the repository root:
 *     node Tools/Documentation/build-docs-viewer.js
 * Verify without writing (exits 1 if the viewer is stale):
 *     node Tools/Documentation/build-docs-viewer.js --check
 */
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..');
const DOCS = path.join(ROOT, 'Documentation');
const OUT = path.join(DOCS, 'ARRISE', 'index.html');

/** Order matters: this is the reading order shown in the sidebar. */
const PAGES = [
  { file: 'ARRISE/README.md', title: 'Overview', group: 'ARRISE Platform' },
  { file: 'ARRISE/ARCHITECTURE.md', title: 'Architecture', group: 'ARRISE Platform' },
  { file: 'ARRISE/HUNT_ENGINE.md', title: 'Hunt Engine', group: 'ARRISE Platform' },
  { file: 'ARRISE/OPERATIONS.md', title: 'Operations Runbook', group: 'ARRISE Platform' },
  { file: 'ARRISE/SCRIPT_CATALOG.md', title: 'Script Catalog', group: 'ARRISE Platform' },
  { file: 'ARRISE/PROJECT_EXPLAINED_HINGLISH.md', title: 'Hinglish Guide', group: 'ARRISE Platform' },
  { file: 'Engineering/README.md', title: 'Handbook Overview', group: 'Dev Guide' },
  { file: 'Engineering/WEBAR-TRAPS.md', title: 'WebAR Traps', group: 'Dev Guide' },
];

function read(rel) {
  const p = path.join(DOCS, rel);
  if (!fs.existsSync(p)) { throw new Error('Missing documentation file: ' + rel); }
  return fs.readFileSync(p, 'utf8');
}

const pages = PAGES.map(p => ({ ...p, id: p.file.replace(/[^a-z0-9]+/gi, '-').toLowerCase(), md: read(p.file) }));

const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ARRISE WebAR Developer Documentation</title>
<style>
  :root{
    color-scheme: dark;
    --bg:#071019; --surface:#0d1925; --surface-2:#132335; --border:#253a4f;
    --text:#eaf2f8; --muted:#9eb1c1; --accent:#25d6c4; --accent-2:#54a6ff;
    --amber:#ffb347; --red:#ff6577; --code:#08131d;
  }
  *{box-sizing:border-box}
  html{scroll-behavior:smooth}
  body{margin:0;background:
      radial-gradient(circle at 12% -8%, rgba(37,214,196,.16), transparent 34rem),
      radial-gradient(circle at 95% 8%, rgba(84,166,255,.13), transparent 30rem),
      var(--bg);
    color:var(--text);font:16px/1.65 "Segoe UI",Inter,system-ui,sans-serif}
  .layout{display:flex;min-height:100vh;align-items:flex-start}
  /* Sidebar */
  aside{position:sticky;top:0;height:100vh;overflow-y:auto;width:290px;flex:0 0 290px;
    background:rgba(9,19,29,.86);border-right:1px solid var(--border);padding:22px 18px}
  .brand{font-weight:800;letter-spacing:.02em;font-size:16px;margin-bottom:2px}
  .brand span{color:var(--accent)}
  .brand-sub{color:var(--muted);font-size:11.5px;letter-spacing:.14em;text-transform:uppercase;margin-bottom:18px}
  .search{width:100%;background:var(--code);border:1px solid var(--border);border-radius:9px;
    color:var(--text);padding:9px 11px;font-size:13.5px;outline:none;margin-bottom:16px}
  .search:focus{border-color:var(--accent)}
  .grp{color:var(--muted);font-size:10.5px;letter-spacing:.16em;text-transform:uppercase;margin:16px 0 7px}
  #nav a{display:block;color:var(--text);text-decoration:none;padding:7px 10px;border-radius:8px;font-size:14px}
  #nav a:hover{background:var(--surface-2)}
  #nav a.on{background:rgba(37,214,196,.14);color:var(--accent);font-weight:600}
  #nav .sub{display:block;padding:4px 10px 4px 22px;font-size:12.5px;color:var(--muted);
    text-decoration:none;border-radius:6px}
  #nav .sub:hover{color:var(--text);background:var(--surface-2)}
  /* Main */
  main{flex:1;min-width:0;padding:34px 42px 96px;max-width:1000px}
  .page{display:none}
  .page.on{display:block}
  h1{font-size:30px;line-height:1.25;margin:0 0 18px;letter-spacing:-.01em}
  h2{font-size:22px;margin:34px 0 12px;padding-top:12px;border-top:1px solid var(--border)}
  h3{font-size:17px;margin:24px 0 8px;color:var(--accent)}
  h4{font-size:15px;margin:18px 0 6px;color:var(--accent-2)}
  p{margin:11px 0}
  a{color:var(--accent-2)}
  ul,ol{margin:10px 0;padding-left:22px}
  li{margin:5px 0}
  code{background:var(--code);border:1px solid var(--border);border-radius:5px;
    padding:1px 5px;font:13px/1.5 ui-monospace,SFMono-Regular,Menlo,monospace}
  pre{background:var(--code);border:1px solid var(--border);border-radius:11px;
    padding:14px 16px;overflow-x:auto;margin:14px 0}
  pre code{background:none;border:none;padding:0;font-size:12.8px;line-height:1.6}
  blockquote{margin:14px 0;padding:12px 16px;background:var(--surface);
    border-left:3px solid var(--accent);border-radius:0 10px 10px 0;color:#d5e3ee}
  blockquote p{margin:6px 0}
  table{border-collapse:collapse;width:100%;margin:14px 0;font-size:14px;display:block;overflow-x:auto}
  th,td{border:1px solid var(--border);padding:9px 12px;text-align:left;vertical-align:top}
  th{background:var(--surface-2);font-size:12.5px;letter-spacing:.04em;text-transform:uppercase;color:var(--muted)}
  tr:nth-child(even) td{background:rgba(255,255,255,.02)}
  hr{border:none;border-top:1px solid var(--border);margin:26px 0}
  /* Severity chips */
  .sev{display:inline-block;font-size:11px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;
    padding:2px 8px;border-radius:20px;margin-right:5px;border:1px solid}
  .sev-breaks{color:var(--red);border-color:rgba(255,101,119,.5);background:rgba(255,101,119,.1)}
  .sev-costly{color:var(--amber);border-color:rgba(255,179,71,.5);background:rgba(255,179,71,.1)}
  .sev-style{color:var(--muted);border-color:var(--border);background:var(--surface-2)}
  .sev-trap{color:var(--accent);border-color:rgba(37,214,196,.5);background:rgba(37,214,196,.1)}
  /* Mermaid-as-flow */
  .flow{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:16px;margin:14px 0}
  .flow-t{color:var(--muted);font-size:10.5px;letter-spacing:.16em;text-transform:uppercase;margin-bottom:12px}
  .flow-row{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin:7px 0}
  .node{background:var(--surface-2);border:1px solid var(--border);border-radius:9px;
    padding:7px 12px;font-size:13px;font-weight:600}
  .arrow{color:var(--accent);font-weight:700}
  .arrow small{color:var(--muted);font-weight:500;font-size:11px}
  /* Search results */
  .hits{margin-top:8px}
  .hit{display:block;padding:8px 10px;border-radius:8px;color:var(--text);text-decoration:none;font-size:13px;
    border:1px solid transparent}
  .hit:hover{background:var(--surface-2);border-color:var(--border)}
  .hit b{color:var(--accent);display:block;font-size:11px;letter-spacing:.1em;text-transform:uppercase;margin-bottom:2px}
  .empty{color:var(--muted);font-size:13px;padding:8px 10px}
  @media(max-width:900px){
    .layout{flex-direction:column}
    aside{position:static;height:auto;width:100%;flex:none;border-right:none;border-bottom:1px solid var(--border)}
    main{padding:24px 18px 70px}
  }
</style>
</head>
<body>
<div class="layout">
  <aside>
    <div class="brand">AR<span>RISE</span></div>
    <div class="brand-sub">WebAR Documentation</div>
    <input class="search" id="q" placeholder="Search all documents..." autocomplete="off">
    <div id="hits" class="hits"></div>
    <div id="nav"></div>
  </aside>
  <main id="main"></main>
</div>
<script>
const PAGES = ${
  // "<" is escaped to \\u003c so a documented "</script>" inside the Markdown
  // cannot terminate this script tag early - our docs really do contain one.
  // U+2028/U+2029 are valid in JSON but are line terminators in JS source.
  JSON.stringify(pages.map(p => ({ id: p.id, title: p.title, group: p.group, md: p.md })))
    .replace(/</g, '\\u003c')
    .replace(/\u2028/g, '\\u2028')
    .replace(/\u2029/g, '\\u2029')
};

/* ---------- Minimal, dependency-free Markdown renderer ----------
   Supports what our docs actually use: headings, tables, fenced code,
   blockquotes, lists, links, bold/italic/inline code, hr. Mermaid flowcharts
   are turned into readable node/arrow rows rather than left as raw source. */
function esc(s){return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}

function inline(s){
  s = esc(s);
  s = s.replace(/\`([^\`]+)\`/g, (m,c)=>'<code>'+c+'</code>');
  s = s.replace(/\\*\\*([^*]+)\\*\\*/g, '<strong>$1</strong>');
  s = s.replace(/(^|[^*])\\*([^*\\n]+)\\*/g, '$1<em>$2</em>');
  s = s.replace(/\\[([^\\]]+)\\]\\(([^)]+)\\)/g, (m,t,h)=>{
    if(/^https?:/.test(h)) return '<a href="'+h+'" target="_blank" rel="noopener">'+t+'</a>';
    const hit = PAGES.find(p=>h.toLowerCase().includes(p.id.split('-').slice(-2,-1)[0]||'@@'));
    const md = h.split('#')[0].split('/').pop();
    const target = PAGES.find(p=>p.id.endsWith(md.replace(/\\./g,'-').toLowerCase()));
    return target ? '<a href="#'+target.id+'" data-nav="'+target.id+'">'+t+'</a>'
                  : '<a href="'+h+'">'+t+'</a>';
  });
  // severity chips
  s = s.replace(/<code>breaks things<\\/code>/g,'<span class="sev sev-breaks">breaks things</span>');
  s = s.replace(/<code>costly<\\/code>/g,'<span class="sev sev-costly">costly</span>');
  s = s.replace(/<code>style<\\/code>/g,'<span class="sev sev-style">style</span>');
  s = s.replace(/<code>junior trap<\\/code>/g,'<span class="sev sev-trap">junior trap</span>');
  return s;
}

/* Turn a mermaid flowchart into readable rows of nodes and arrows. */
function flow(src){
  const label = {};
  const rows = [];
  src.split('\\n').forEach(raw=>{
    const line = raw.trim();
    if(!line || /^(flowchart|graph|subgraph|end|direction)/.test(line)) return;
    // capture id["Label"] definitions anywhere on the line
    line.replace(/([A-Za-z0-9_]+)\\[(?:"|)([^\\]"]+)(?:"|)\\]/g,(m,id,txt)=>{label[id]=txt.replace(/<br\\/?>/g,' ');return m;});
    line.replace(/([A-Za-z0-9_]+)\\((?:"|)([^)"]+)(?:"|)\\)/g,(m,id,txt)=>{label[id]=txt.replace(/<br\\/?>/g,' ');return m;});
    if(line.includes('--')){
      const parts = line.split(/\\s*-->\\s*|\\s*---\\s*/);
      const edge = (line.match(/--\\s*"?([^">|]+)"?\\s*-->/)||line.match(/\\|([^|]+)\\|/)||[])[1];
      rows.push({parts:parts.map(p=>{
        const id=(p.match(/^([A-Za-z0-9_]+)/)||[])[1]||p.trim();
        return label[id]||id;
      }), edge:edge?edge.trim():''});
    }
  });
  if(!rows.length) return '<pre><code>'+esc(src)+'</code></pre>';
  return '<div class="flow"><div class="flow-t">Flow</div>'+rows.map(r=>
    '<div class="flow-row">'+r.parts.map(p=>'<span class="node">'+esc(p)+'</span>')
      .join('<span class="arrow">-&gt;'+(r.edge?' <small>'+esc(r.edge)+'</small>':'')+'</span>')+'</div>'
  ).join('')+'</div>';
}

function render(md){
  const out=[]; const lines=md.split('\\n'); let i=0;
  while(i<lines.length){
    const L=lines[i];
    // fenced code
    if(/^\`\`\`/.test(L)){
      const lang=L.slice(3).trim(); const buf=[]; i++;
      while(i<lines.length && !/^\`\`\`/.test(lines[i])) buf.push(lines[i++]);
      i++;
      out.push(lang==='mermaid' ? flow(buf.join('\\n'))
        : '<pre><code>'+esc(buf.join('\\n'))+'</code></pre>');
      continue;
    }
    // table
    if(/^\\|/.test(L) && /^\\|[\\s:|-]+\\|$/.test(lines[i+1]||'')){
      const head=L.split('|').slice(1,-1).map(c=>c.trim()); i+=2;
      const body=[];
      while(i<lines.length && /^\\|/.test(lines[i])) body.push(lines[i++].split('|').slice(1,-1).map(c=>c.trim()));
      out.push('<table><thead><tr>'+head.map(h=>'<th>'+inline(h)+'</th>').join('')+'</tr></thead><tbody>'+
        body.map(r=>'<tr>'+r.map(c=>'<td>'+inline(c)+'</td>').join('')+'</tr>').join('')+'</tbody></table>');
      continue;
    }
    // blockquote
    if(/^>/.test(L)){
      const buf=[];
      while(i<lines.length && /^>/.test(lines[i])) buf.push(lines[i++].replace(/^>\\s?/,''));
      out.push('<blockquote>'+render(buf.join('\\n'))+'</blockquote>');
      continue;
    }
    // heading
    const h=L.match(/^(#{1,6})\\s+(.*)$/);
    if(h){ const n=h[1].length; const id=h[2].toLowerCase().replace(/[^a-z0-9]+/g,'-');
      out.push('<h'+n+' id="'+id+'">'+inline(h[2])+'</h'+n+'>'); i++; continue; }
    // hr
    if(/^---+$/.test(L)){ out.push('<hr>'); i++; continue; }
    // lists
    if(/^\\s*([-*]|\\d+\\.)\\s+/.test(L)){
      const ordered=/^\\s*\\d+\\./.test(L); const buf=[];
      while(i<lines.length && /^\\s*([-*]|\\d+\\.)\\s+/.test(lines[i])){
        buf.push(lines[i].replace(/^\\s*([-*]|\\d+\\.)\\s+/,'')); i++;
        while(i<lines.length && /^\\s{2,}\\S/.test(lines[i]) && !/^\\s*([-*]|\\d+\\.)\\s+/.test(lines[i])){
          buf[buf.length-1]+=' '+lines[i].trim(); i++;
        }
      }
      out.push('<'+(ordered?'ol':'ul')+'>'+buf.map(b=>'<li>'+inline(b)+'</li>').join('')+'</'+(ordered?'ol':'ul')+'>');
      continue;
    }
    if(!L.trim()){ i++; continue; }
    // paragraph
    const buf=[];
    while(i<lines.length && lines[i].trim() && !/^(#{1,6}\\s|\\||>|\`\`\`|---+$|\\s*([-*]|\\d+\\.)\\s)/.test(lines[i])) buf.push(lines[i++]);
    out.push('<p>'+inline(buf.join(' '))+'</p>');
  }
  return out.join('\\n');
}

/* ---------- Build the page ---------- */
const main=document.getElementById('main'), navEl=document.getElementById('nav');
const groups=[...new Set(PAGES.map(p=>p.group))];
navEl.innerHTML=groups.map(g=>'<div class="grp">'+g+'</div>'+
  PAGES.filter(p=>p.group===g).map(p=>'<a href="#'+p.id+'" data-nav="'+p.id+'">'+p.title+'</a>').join('')
).join('');
main.innerHTML=PAGES.map(p=>'<article class="page" id="'+p.id+'">'+render(p.md)+'</article>').join('');

function show(id){
  const t=PAGES.find(p=>p.id===id)||PAGES[0];
  document.querySelectorAll('.page').forEach(e=>e.classList.toggle('on',e.id===t.id));
  document.querySelectorAll('#nav a').forEach(a=>a.classList.toggle('on',a.dataset.nav===t.id));
  window.scrollTo(0,0);
}
document.addEventListener('click',e=>{
  const a=e.target.closest('[data-nav]');
  if(a){ e.preventDefault(); location.hash=a.dataset.nav; show(a.dataset.nav); }
});
window.addEventListener('hashchange',()=>show(location.hash.slice(1)));
show(location.hash.slice(1)||PAGES[0].id);

/* ---------- Search ---------- */
const q=document.getElementById('q'), hits=document.getElementById('hits');
q.addEventListener('input',()=>{
  const v=q.value.trim().toLowerCase();
  if(v.length<2){hits.innerHTML='';return;}
  const found=[];
  PAGES.forEach(p=>{
    p.md.split('\\n').forEach(line=>{
      if(found.length<25 && line.toLowerCase().includes(v) && line.trim().length>3){
        found.push({id:p.id,title:p.title,line:line.replace(/^#+\\s*/,'').replace(/[|*\`>]/g,'').trim().slice(0,95)});
      }
    });
  });
  hits.innerHTML = found.length
    ? found.map(f=>'<a class="hit" href="#'+f.id+'" data-nav="'+f.id+'"><b>'+f.title+'</b>'+esc(f.line)+'</a>').join('')
    : '<div class="empty">No matches.</div>';
});
</script>
</body>
</html>
`;

if (process.argv.includes('--check')) {
  const cur = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8') : '';
  if (cur !== html) {
    console.error('STALE: Documentation/ARRISE/index.html does not match the Markdown sources.');
    console.error('Run: node Tools/Documentation/build-docs-viewer.js');
    process.exit(1);
  }
  console.log('OK: viewer is up to date with all ' + pages.length + ' documents.');
  process.exit(0);
}

fs.writeFileSync(OUT, html);
console.log('Wrote ' + path.relative(ROOT, OUT) + ' from ' + pages.length + ' documents (' +
  (html.length / 1024).toFixed(0) + ' KB).');
