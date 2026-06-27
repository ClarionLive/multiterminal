/*
 * search.js — instant client-side search for the MultiTerminal docs.
 * Reads window.MT_DOCS (from the generated search-index.js) and drives the
 * sidebar search box (#mt-search) with a dropdown of matching sections.
 * Pure client-side so it works when the docs are opened as local files.
 */
(function () {
  var input, panel, entries = [], results = [], sel = -1;

  function esc(s) {
    return String(s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  function buildEntries() {
    (window.MT_DOCS || []).forEach(function (d) {
      (d.s || []).forEach(function (sec) {
        var text = sec.x || '';
        entries.push({
          u: d.u,
          id: sec.id || '',
          page: d.t || d.u,
          sec: sec.t || '',
          text: text,
          hay: (d.t + ' ' + (sec.t || '') + ' ' + text).toLowerCase()
        });
      });
    });
  }

  function search(q) {
    q = q.trim().toLowerCase();
    results = [];
    if (!q) return;
    var tokens = q.split(/\s+/).filter(Boolean);
    var scored = [];
    entries.forEach(function (e) {
      if (!tokens.every(function (t) { return e.hay.indexOf(t) >= 0; })) return;
      var score = 0;
      tokens.forEach(function (t) {
        if (e.page.toLowerCase().indexOf(t) >= 0) score += 8;
        if (e.sec.toLowerCase().indexOf(t) >= 0) score += 5;
        score += e.text.toLowerCase().split(t).length - 1;
      });
      scored.push({ e: e, score: score });
    });
    scored.sort(function (a, b) { return b.score - a.score; });
    results = scored.slice(0, 12).map(function (x) { return x.e; });
  }

  function snippet(text, tok) {
    if (!text) return '';
    var i = tok ? text.toLowerCase().indexOf(tok) : -1;
    if (i < 0) return text.slice(0, 100) + (text.length > 100 ? '…' : '');
    var start = Math.max(0, i - 35), end = Math.min(text.length, i + tok.length + 60);
    return (start > 0 ? '…' : '') + text.slice(start, end) + (end < text.length ? '…' : '');
  }

  function highlight(s, tokens) {
    var out = esc(s);
    tokens.forEach(function (t) {
      if (!t) return;
      var re = new RegExp('(' + t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'ig');
      out = out.replace(re, '<mark>$1</mark>');
    });
    return out;
  }

  function render(q) {
    var tokens = q.trim().toLowerCase().split(/\s+/).filter(Boolean);
    if (!q.trim()) { panel.hidden = true; panel.innerHTML = ''; return; }
    if (!results.length) {
      panel.hidden = false;
      panel.innerHTML = '<div class="doc-search-empty">No matches for &ldquo;' + esc(q) + '&rdquo;</div>';
      return;
    }
    var firstTok = tokens[0] || '';
    panel.innerHTML = results.map(function (e, i) {
      var label = e.sec && e.sec !== e.page ? e.page + ' › ' + e.sec : e.page;
      var href = e.u + (e.id ? '#' + e.id : '');
      return '<a class="doc-search-result' + (i === sel ? ' active' : '') + '" href="' + href + '">' +
        '<span class="dsr-title">' + highlight(label, tokens) + '</span>' +
        '<span class="dsr-snippet">' + highlight(snippet(e.text, firstTok), tokens) + '</span></a>';
    }).join('');
    panel.hidden = false;
  }

  function onInput() { search(input.value); sel = -1; render(input.value); }

  function move(d) {
    if (panel.hidden || !results.length) return;
    sel = (sel + d + results.length) % results.length;
    render(input.value);
    var a = panel.children[sel];
    if (a && a.scrollIntoView) a.scrollIntoView({ block: 'nearest' });
  }

  function go() {
    var e = sel >= 0 ? results[sel] : results[0];
    if (e) location.href = e.u + (e.id ? '#' + e.id : '');
  }

  function init() {
    input = document.getElementById('mt-search');
    panel = document.getElementById('mt-search-results');
    if (!input || !panel) return;
    buildEntries();

    input.addEventListener('input', onInput);
    input.addEventListener('focus', function () { if (input.value.trim()) onInput(); });
    input.addEventListener('keydown', function (ev) {
      if (ev.key === 'ArrowDown') { ev.preventDefault(); move(1); }
      else if (ev.key === 'ArrowUp') { ev.preventDefault(); move(-1); }
      else if (ev.key === 'Enter') { ev.preventDefault(); go(); }
      else if (ev.key === 'Escape') { input.value = ''; panel.hidden = true; input.blur(); }
    });
    document.addEventListener('click', function (ev) {
      if (ev.target !== input && !panel.contains(ev.target)) panel.hidden = true;
    });
    // Press "/" anywhere to jump to search.
    document.addEventListener('keydown', function (ev) {
      if (ev.key === '/' && document.activeElement !== input &&
        !/^(INPUT|TEXTAREA|SELECT)$/.test((document.activeElement || {}).tagName || '')) {
        ev.preventDefault(); input.focus();
      }
    });
  }

  if (document.readyState !== 'loading') init();
  else document.addEventListener('DOMContentLoaded', init);
})();
