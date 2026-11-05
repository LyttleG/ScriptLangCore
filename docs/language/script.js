/* ============================================================
   ScriptLangCore, Language Reference Search & Navigation
   Vanilla JS, no dependencies
   ============================================================ */

(function () {
  'use strict';

  // ── State ──────────────────────────────────────────────
  const state = {
    query: '',
    matchCount: 0,
    totalCount: 0,
    // What the reader had open before a search, restored when the box is cleared.
    searchSnapshot: null,
    // Every occurrence marked for the current query, and which one is current.
    hits: [],
    hitIndex: -1,
  };

  // ── DOM refs ───────────────────────────────────────────
  const searchInput = document.getElementById('search');
  const searchStats = document.getElementById('searchStats');
  const noResults = document.getElementById('noResults');
  const sidebarLinks = document.querySelectorAll('.sidebar a');
  const categoryTitles = document.querySelectorAll('.category h2');
  const entries = document.querySelectorAll('.entry');
  // Auto-assign id attributes matching data-entry-id for native hash navigation
  entries.forEach(function(e) { var id = e.dataset.entryId; if (id && !e.id) e.id = id; });
  const entryList = document.querySelectorAll('.entry-list');

  // ── Helpers ────────────────────────────────────────────

  /** Normalize text for search matching */
  function normalize(text) {
    // Keep '.' so namespaced function keywords (e.g. "Coroutine.Start", "Z.Math.add")
    // survive normalization and match the same spelling inside .fn spans.
    return text.toLowerCase().replace(/[^a-z0-9\s_.<>]/g, '');
  }

  /** Check if entry matches the search query */
  function matches(entry, query) {
    if (!query) return true;
    const nq = normalize(query);
    const name = entry.querySelector('.entry-name')?.textContent ?? '';
    const desc = entry.querySelector('.entry-desc')?.textContent ?? '';
    const body = entry.querySelector('.entry-body')?.textContent ?? '';
    // Explicitly index .fn (function names) and keyword/type/code spans so generated
    // keywords are searchable even when they only appear inside highlighted code
    // (e.g. "Coroutine.Start", "Z.Math.add", "MoveNext").
    const fnTokens = Array.from(entry.querySelectorAll('.fn, .kw, .typ, code'))
      .map(el => el.textContent ?? '')
      .join(' ');
    const full = name + ' ' + desc + ' ' + body + ' ' + fnTokens;
    return normalize(full).includes(nq);
  }

  /** Get the category this entry belongs to */
  function getCategory(entry) {
    return entry.closest('.category');
  }

  /** Scroll to an entry and open its category, scoping to namespace children */
  function focusEntry(name) {
    entries.forEach(e => e.classList.remove('open'));
    var target = null;
    for (var i = 0; i < entries.length; i++) {
      if (entries[i].dataset.entryId === name) { target = entries[i]; break; }
    }
    if (!target) {
      for (var i = 0; i < entries.length; i++) {
        var en = entries[i].querySelector('.entry-name');
        if (en && en.textContent.trim() === name) { target = entries[i]; break; }
      }
    }
    if (target) {
      var cat = target.closest('.category');
      if (cat) cat.classList.remove('collapsed');
      target.classList.add('open');
      requestAnimationFrame(function() { requestAnimationFrame(function() { target.scrollIntoView({ behavior: 'smooth', block: 'center' }); }); });
      sidebarLinks.forEach(function(a) { a.classList.remove('active'); });
      var link = document.querySelector('.sidebar a[href="#' + name + '"]');
      if (link) link.classList.add('active');

      // Scope: if the entry has namespace children (entries with data-entry-id starting with "name-"),
      // filter to show only that namespace
      var prefix = name + '-';
      var hasChildren = false;
      entries.forEach(function(e) {
        if (e.dataset.entryId && e.dataset.entryId.startsWith(prefix)) { hasChildren = true; }
      });
      if (hasChildren) {
        // Clear search input but apply namespace filter
        searchInput.value = '';
        entries.forEach(function(e) {
          var match = (e === target) || (e.dataset.entryId && e.dataset.entryId.startsWith(prefix));
          e.classList.toggle('hidden', !match);
        });
        categoryTitles.forEach(function(title) {
          var c = title.closest('.category');
          if (!c) return;
          var visible = c.querySelectorAll('.entry:not(.hidden)').length > 0;
          c.classList.toggle('hidden', !visible);
        });
        noResults.style.display = 'none'; // hide no-results when scoping
        var visibleCount = document.querySelectorAll('.entry:not(.hidden)').length;
        searchStats.textContent = visibleCount + ' entries in ' + name;
      }
    }

    // Fallback: non-entry anchors (e.g. an h4 inside an entry), scroll to the element,
    // open its containing entry/category, and highlight the sidebar link.
    if (!target) {
      var anchor = document.getElementById(name);
      if (anchor) {
        var host = anchor.closest('.entry');
        if (host) {
          host.classList.add('open');
          var cat = host.closest('.category');
          if (cat) cat.classList.remove('collapsed');
        }
        requestAnimationFrame(function() { requestAnimationFrame(function() { anchor.scrollIntoView({ behavior: 'smooth', block: 'start' }); }); });
        sidebarLinks.forEach(function(a) { a.classList.remove('active'); });
        var link = document.querySelector('.sidebar a[href="#' + name + '"]');
        if (link) link.classList.add('active');
      }
    }
  }

  // ── Match highlighting ─────────────────────────────────
  // Every occurrence of what was typed, marked the way a browser's own find marks it. ONLY TEXT NODES are
  // rewritten: this page's syntax highlighting lives in spans, and a string replace over innerHTML is precisely
  // what corrupted several thousand of them here once, so markup is never rebuilt. A match that SPANS two nodes
  // (a keyword and the call beside it) is deliberately not marked, because wrapping it would mean rewriting the
  // markup in between; a single word, which is what a search is for, sits in one node.

  /** Remove every mark, putting the text back exactly as it was. */
  function clearHighlights() {
    document.querySelectorAll('mark.hit').forEach(mark => {
      const parent = mark.parentNode;
      if (!parent) return;
      parent.replaceChild(document.createTextNode(mark.textContent), mark);
      parent.normalize();
    });
    state.hits = [];
    state.hitIndex = -1;
  }

  /** Mark every occurrence of `needle` inside `root`, collecting them in state.hits. */
  function markTextNodes(root, needle) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        const parent = node.parentNode;
        if (!node.nodeValue || !node.nodeValue.trim() || !parent) return NodeFilter.FILTER_REJECT;
        const tag = parent.nodeName;
        if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'MARK') return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      },
    });
    const nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);

    nodes.forEach(node => {
      const text = node.nodeValue;
      const low = text.toLowerCase();
      let at = low.indexOf(needle);
      let from = 0;
      let marked = false;
      const frag = document.createDocumentFragment();
      while (at >= 0) {
        if (at > from) frag.appendChild(document.createTextNode(text.slice(from, at)));
        const mark = document.createElement('mark');
        mark.className = 'hit';
        // The text as the page spells it, so searching in one case marks the other.
        mark.textContent = text.slice(at, at + needle.length);
        frag.appendChild(mark);
        state.hits.push(mark);
        marked = true;
        from = at + needle.length;
        at = low.indexOf(needle, from);
      }
      if (!marked) return;
      if (from < text.length) frag.appendChild(document.createTextNode(text.slice(from)));
      node.parentNode.replaceChild(frag, node);
    });
  }

  /** Mark the matches of a query, and make the first of them the current one. */
  function highlightMatches(query) {
    if (!query) return;
    const needle = query.toLowerCase();
    document.querySelectorAll('.entry:not(.hidden)').forEach(entry => markTextNodes(entry, needle));
    if (state.hits.length) {
      state.hitIndex = 0;
      state.hits[0].classList.add('current');
    }
  }

  /** Step to the next (or previous) match, the way F3 and Shift+F3 do in a browser. */
  function moveHit(step) {
    if (!state.hits.length) return;
    state.hits[state.hitIndex]?.classList.remove('current');
    state.hitIndex = (state.hitIndex + step + state.hits.length) % state.hits.length;
    const hit = state.hits[state.hitIndex];
    hit.classList.add('current');
    hit.scrollIntoView({ block: 'center', behavior: 'smooth' });
    searchStats.textContent = `${state.matchCount} / ${entries.length} results · ${state.hitIndex + 1}/${state.hits.length} matches`;
  }

  // ── Search ─────────────────────────────────────────────
  function doSearch() {
    const query = searchInput.value.trim();
    state.query = query;
    let visibleCount = 0;
    clearHighlights();

    entries.forEach(entry => {
      const match = matches(entry, query);
      entry.classList.toggle('hidden', !match);
      if (match) visibleCount++;
    });

    // What was open BEFORE this search, taken once, so clearing the box puts the reader's own state back. A
    // search is a look rather than a rearrangement: without this, one search would leave every matching entry
    // open for good.
    if (query && !state.searchSnapshot) {
      state.searchSnapshot = {
        open: Array.from(document.querySelectorAll('.entry.open')),
        collapsed: Array.from(document.querySelectorAll('.category.collapsed')),
      };
    }

    // Show/hide categories
    categoryTitles.forEach(title => {
      const cat = title.closest('.category');
      if (!cat) return;
      const visible = cat.querySelectorAll('.entry:not(.hidden)').length > 0;
      cat.classList.toggle('hidden', !visible && query.length > 0);
    });

    // No results
    const hasResults = visibleCount > 0;
    noResults.classList.toggle('visible', !hasResults && query.length > 0);

    // Stats
    state.matchCount = visibleCount;
    state.totalCount = entries.length;
    if (query) {
      searchStats.textContent = `${visibleCount} / ${entries.length} results`;
    } else {
      searchStats.textContent = `${entries.length} entries`;
    }

    // Auto-expand all visible categories when searching
    if (query) {
      document.querySelectorAll('.category:not(.hidden)').forEach(cat => {
        cat.classList.remove('collapsed');
        const toggle = cat.querySelector('.toggle');
        if (toggle) toggle.classList.remove('collapsed');
      });
      // ...and OPEN every entry the search kept. This is the half that was missing: a hit inside an entry's BODY
      // was invisible until that entry was clicked, one at a time, on a page of several hundred entries. The CSS
      // is what made it invisible (`.entry-body` is `display: none` until the entry carries `open`).
      document.querySelectorAll('.entry:not(.hidden)').forEach(entry => {
        entry.classList.add('open');
      });
    } else if (state.searchSnapshot) {
      // The box is empty: put back exactly what was open and what was collapsed before any search.
      document.querySelectorAll('.entry.open').forEach(entry => entry.classList.remove('open'));
      state.searchSnapshot.open.forEach(entry => entry.classList.add('open'));
      document.querySelectorAll('.category.collapsed').forEach(cat => {
        cat.classList.remove('collapsed');
        const toggle = cat.querySelector('.toggle');
        if (toggle) toggle.classList.remove('collapsed');
      });
      state.searchSnapshot.collapsed.forEach(cat => {
        cat.classList.add('collapsed');
        const toggle = cat.querySelector('.toggle');
        if (toggle) toggle.classList.add('collapsed');
      });
      state.searchSnapshot = null;
    }

    // Mark what was typed, and say how many occurrences there are, since the entry count alone does not say
    // whether the match is on this screen or four below it.
    highlightMatches(query);
    if (query && state.hits.length) {
      searchStats.textContent = `${visibleCount} / ${entries.length} results · ${state.hits.length} matches`;
    }
  }

  // ── Category toggle ────────────────────────────────────
  categoryTitles.forEach(title => {
    title.addEventListener('click', () => {
      const cat = title.closest('.category');
      if (!cat) return;
      cat.classList.toggle('collapsed');
      const toggle = title.querySelector('.toggle');
      if (toggle) toggle.classList.toggle('collapsed');
    });
  });

  // ── Entry toggle via main content click ───────────────
  entries.forEach(entry => {
    const id = entry.dataset.entryId || entry.querySelector('.entry-name')?.textContent?.trim() || Math.random().toString();
    entry.dataset.entryId = id;

    entry.addEventListener('click', (e) => {
      e.stopPropagation();
      // Don't toggle if user selected text
      if (window.getSelection()?.toString()) return;
      // Delegate to focusEntry so main content clicks behave identically to sidebar clicks
      const name = entry.querySelector('.entry-name')?.textContent?.trim();
      if (name) focusEntry(name);
    });
  });

  // ── Sidebar navigation ─────────────────────────────────
  sidebarLinks.forEach(link => {
    link.addEventListener('click', (e) => {
      e.preventDefault();
      // Clearing the box through doSearch shows every entry again AND restores the state the reader had before
      // any search, which is one code path for "no query" rather than a second copy of the unhiding.
      searchInput.value = '';
      doSearch();
      const href = link.getAttribute('href');
      const name = href.startsWith('#') ? href.substring(1) : href;
      focusEntry(name);
    });
  });

  // ── Keyboard shortcuts ─────────────────────────────────
  document.addEventListener('keydown', (e) => {
    // Ctrl+F or / to focus search
    if ((e.ctrlKey && e.key === 'f') || (e.key === '/' && !['INPUT', 'TEXTAREA'].includes(document.activeElement?.tagName || ''))) {
      e.preventDefault();
      searchInput.focus();
    }
    // Escape to clear search and scope
    if (e.key === 'Escape' && document.activeElement === searchInput) {
      searchInput.value = '';
      doSearch();
      searchInput.blur();
    }
    if (e.key === 'Escape' && document.activeElement !== searchInput) {
      // Clear any namespace scope
      searchInput.value = '';
      doSearch();
    }
    // F3 and Shift+F3 step through the matches, as in a browser's own find. It works with the box focused or not,
    // which is what makes it a find rather than another thing the input owns.
    if (e.key === 'F3' && state.hits.length) {
      e.preventDefault();
      moveHit(e.shiftKey ? -1 : 1);
    }
    // Enter on search goes to the best result: prefer an entry whose NAME matches the
    // query (e.g. "print" → the print entry, not the first body-match like "int"),
    // then fall back to the first entry that contains the query anywhere.
    if (e.key === 'Enter' && document.activeElement === searchInput) {
      const nq = normalize(searchInput.value.trim());
      let first = null;
      if (nq) {
        first = Array.from(document.querySelectorAll('.entry:not(.hidden)')).find(ent => {
          const name = ent.querySelector('.entry-name')?.textContent ?? '';
          return normalize(name).includes(nq);
        });
      }
      if (!first) first = document.querySelector('.entry:not(.hidden) .entry-name');
      if (first) {
        const nameEl = first.classList.contains('entry-name') ? first : first.querySelector('.entry-name');
        if (nameEl) focusEntry(nameEl.textContent.trim());
      }
    }
  });

  // ── Hash-based deep linking ────────────────────────────
  function handleHash() {
    const hash = window.location.hash.substring(1);
    if (hash) {
      // Clear search
      searchInput.value = '';
      doSearch();
      focusEntry(hash);
    }
  }

  window.addEventListener('hashchange', handleHash);

  // ── Clipboard copy ─────────────────────────────────────
  function addCopyButtons() {
    document.querySelectorAll('.syntax, .example').forEach(el => {
      // Skip if already wrapped
      if (el.parentElement?.classList.contains('code-block')) return;
      const wrapper = document.createElement('div');
      wrapper.className = 'code-block';
      el.parentElement?.insertBefore(wrapper, el);
      wrapper.appendChild(el);

      const btn = document.createElement('button');
      btn.className = 'copy-btn';
      btn.innerHTML = '&#x2398;'; // ⎘ clipboard symbol
      btn.title = 'Copy to clipboard';
      btn.addEventListener('click', async (e) => {
        e.stopPropagation(); // prevent entry toggle
        // Get only the text content (strip HTML tags)
        const text = el.textContent || '';
        try {
          await navigator.clipboard.writeText(text);
          btn.classList.add('copied');
          btn.innerHTML = '&#x2713;'; // checkmark
          setTimeout(() => {
            btn.classList.remove('copied');
            btn.innerHTML = '&#x2398;';
          }, 2000);
        } catch {
          // Fallback for older browsers
          const ta = document.createElement('textarea');
          ta.value = text;
          ta.style.position = 'fixed'; ta.style.left = '-9999px';
          document.body.appendChild(ta);
          ta.select();
          document.execCommand('copy');
          document.body.removeChild(ta);
          btn.classList.add('copied');
          btn.innerHTML = '&#x2713;';
          setTimeout(() => {
            btn.classList.remove('copied');
            btn.innerHTML = '&#x2398;';
          }, 2000);
        }
      });
      wrapper.appendChild(btn);
    });
  }

  function addNoteCopyButtons() {
    document.querySelectorAll('.entry-body .note').forEach(el => {
      if (el.querySelector('.copy-btn')) return;
      const btn = document.createElement('button');
      btn.className = 'copy-btn';
      btn.innerHTML = '&#x2398;';
      btn.title = 'Copy note text';
      btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        const text = el.textContent?.replace(/⎘|✓/g, '').trim() || '';
        try {
          await navigator.clipboard.writeText(text);
          btn.classList.add('copied');
          btn.innerHTML = '&#x2713;';
          setTimeout(() => {
            btn.classList.remove('copied');
            btn.innerHTML = '&#x2398;';
          }, 2000);
        } catch {
          const ta = document.createElement('textarea');
          ta.value = text;
          ta.style.position = 'fixed'; ta.style.left = '-9999px';
          document.body.appendChild(ta);
          ta.select();
          document.execCommand('copy');
          document.body.removeChild(ta);
          btn.classList.add('copied');
          btn.innerHTML = '&#x2713;';
          setTimeout(() => {
            btn.classList.remove('copied');
            btn.innerHTML = '&#x2398;';
          }, 2000);
        }
      });
      el.appendChild(btn);
    });
  }

  // ── Collapse / Expand all ──────────────────────────────
  document.getElementById('collapseAll')?.addEventListener('click', () => {
    document.querySelectorAll('.category').forEach(cat => {
      cat.classList.add('collapsed');
      const toggle = cat.querySelector('.toggle');
      if (toggle) toggle.classList.add('collapsed');
    });
    // Close any open entry
    entries.forEach(e => e.classList.remove('open'));
    state.openEntries.clear();
    sidebarLinks.forEach(a => a.classList.remove('active'));
  });

  document.getElementById('expandAll')?.addEventListener('click', () => {
    document.querySelectorAll('.category').forEach(cat => {
      cat.classList.remove('collapsed');
      const toggle = cat.querySelector('.toggle');
      if (toggle) toggle.classList.remove('collapsed');
    });
  });

  // ── Init ───────────────────────────────────────────────
  searchInput.addEventListener('input', doSearch);
  addCopyButtons();
  addNoteCopyButtons();
  doSearch();
  handleHash();

  // ── Code-as-link: click <code> to navigate to its docs ──
  function findEntryByName(name) {
    var n = name.trim();
    // Direct match on data-entry-id
    for (var i = 0; i < entries.length; i++) {
      if (entries[i].dataset.entryId === n) return entries[i];
    }
    // Match on .entry-name text
    for (var i = 0; i < entries.length; i++) {
      var en = entries[i].querySelector('.entry-name');
      if (en && en.textContent.trim() === n) return entries[i];
    }
    // Case-insensitive fallback
    var nl = n.toLowerCase();
    for (var i = 0; i < entries.length; i++) {
      var en = entries[i].querySelector('.entry-name');
      if (en && en.textContent.trim().toLowerCase() === nl) return entries[i];
    }
    return null;
  }

  document.querySelectorAll('.entry-body code').forEach(function(el) {
    el.addEventListener('click', function(e) {
      e.stopPropagation();
      if (window.getSelection()?.toString()) return;
      var id = el.dataset.entryId || el.textContent.trim();
      var entry = findEntryByName(id);
      if (entry) {
        var cat = entry.closest('.category');
        if (cat) cat.classList.remove('collapsed');
        entry.classList.add('open');
        requestAnimationFrame(function() {
          requestAnimationFrame(function() {
            entry.scrollIntoView({ behavior: 'smooth', block: 'center' });
          });
        });
        sidebarLinks.forEach(function(a) { a.classList.remove('active'); });
        var link = document.querySelector('.sidebar a[href="#' + entry.dataset.entryId + '"]');
        if (link) link.classList.add('active');
      }
    });
    el.style.cursor = 'pointer';
    el.title = 'Click to view documentation';
  });

})();
