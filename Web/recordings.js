/*
 * Episode History — recordings views (SageTV's Recordings view, Channels DVR's Recordings
 * section): what the DVR recorded, newest first, one card per show, with an All / Current /
 * Archived filter. "Archived" recordings are ones since moved out of the recordings folders
 * (e.g. by JellyDVR Space); they stay listed, as in SageTV. Shown as
 *   - a row on the home screen, and
 *   - a grid at the top of Live TV > Recordings, replacing (or above) Jellyfin's "Recently
 *     Added Recordings", which lists everything in the libraries holding the recordings
 *     folders, Channels DVR files and archives included. "All Recordings" (library tiles)
 *     stays as the everything view.
 *
 * Data and settings come from GET /EpisodeHistory/Recordings. Loaded by guidemenu.js, so no
 * extra index.html patch is needed.
 *
 * VERSION-SENSITIVE FILE — built against this server's jellyfin-web 12.0.0. Confirmed live
 * via DOM inspection on 2026-09-28:
 *   - Home rows live in `.homeSectionsContainer`; each child is `div.sectionN` (or
 *     `div.verticalSection.sectionN`), numbered by position, not by kind, so rows are
 *     recognised by their headings ("Continue Watching", "Next Up", "Recently Added in …").
 *   - A row is `div.verticalSection.emby-scroller-container` > `div.sectionTitleContainer
 *     .sectionTitleContainer-cards.padded-left` > `h2.sectionTitle.sectionTitle-cards`, then
 *     `div[is=emby-scroller]` > `div[is=emby-itemscontainer].itemsContainer.scrollSlider`.
 *   - Portrait cards are `div.card.overflowPortraitCard` > `.cardBox.cardBox-bottompadded` >
 *     `.cardScalable` (`.cardPadder.cardPadder-overflowPortrait` + `.cardImageContainer`),
 *     then `.cardFooter` with `.cardText` lines.
 *   - Live TV > Recordings is React: `#/livetv?tab=3`, `div#liveTvPage` holding
 *     `div.verticalSection` children headed "Recently Added Recordings" and "All Recordings".
 *     Other Live TV tabs reuse the same page element.
 * If this breaks after a jellyfin-web update, re-check these against the running client.
 */
(function () {
  'use strict';

  var SECTION_CLASS = 'episodeHistoryRecordings';
  var FILTER_KEY = 'episodeHistory.recordingsFilter';
  var MAX_AGE_MS = 30000;
  var FILTERS = [
    { id: 'all', label: 'All' },
    { id: 'current', label: 'Current' },
    { id: 'archived', label: 'Archived' }
  ];

  var cache = {};        // filter|limit -> { at, data }
  var inflight = {};     // filter|limit -> pending request
  var settings = null;   // row/tab settings from the last response
  var loading = {};      // view name -> true while its first load runs

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[EpisodeHistory] ' + msg);
    }
  }

  function storedFilter() {
    try {
      return window.localStorage.getItem(FILTER_KEY);
    } catch (e) {
      return null;
    }
  }

  function saveFilter(filter) {
    try {
      window.localStorage.setItem(FILTER_KEY, filter);
    } catch (e) {
      // Private mode: the choice lasts until the page reloads.
    }
  }

  function currentFilter() {
    return storedFilter() || (settings && settings.DefaultFilter) || 'all';
  }

  function key(filter, limit) {
    return filter + '|' + (limit || '');
  }

  function fetchCards(filter, limit) {
    var k = key(filter, limit);
    if (inflight[k]) {
      return inflight[k];
    }
    var apiClient = window.ApiClient;
    var query = { filter: filter };
    if (limit) {
      query.limit = limit;
    }
    inflight[k] = apiClient.ajax({
      type: 'GET',
      url: apiClient.getUrl('EpisodeHistory/Recordings', query),
      dataType: 'json'
    }).then(function (data) {
      delete inflight[k];
      settings = (data && data.Settings) || settings;
      cache[k] = { at: Date.now(), data: data };
      return data;
    }, function (err) {
      delete inflight[k];
      // Don't retry on every DOM change after a failure.
      cache[k] = { at: Date.now(), data: (cache[k] && cache[k].data) || null };
      throw err;
    });
    return inflight[k];
  }

  function heading(section) {
    var h = section.querySelector('h2');
    return h ? h.textContent.trim() : '';
  }

  /*
   * Where recordings are shown. Each view finds its container (null when not on that
   * page), says whether it's switched on, and places the section.
   */
  var VIEWS = [
    {
      // Home screen: one scrolling row among jellyfin-web's own.
      name: 'home',
      layout: 'row',
      limit: function () { return null; },
      container: function () {
        if ((window.location.hash || '').indexOf('#/home') !== 0) {
          return null;
        }
        var container = document.querySelector('.page:not(.hide) .homeSectionsContainer');
        // Wait for jellyfin-web's own rows, so ours can be placed among them.
        return container && container.offsetParent !== null && container.querySelector('.sectionTitle') ? container : null;
      },
      enabled: function (s) { return !!s.Enabled; },
      title: function (s) { return s.Title || 'Recorded Shows'; },
      place: placeOnHome
    },
    {
      // Live TV > Recordings: a full grid at the top, instead of (or above) Jellyfin's
      // "Recently Added Recordings", which lists everything in the libraries that hold the
      // recordings folders (Channels DVR files and archives included).
      name: 'tab',
      layout: 'grid',
      limit: function (s) { return s.TabLimit || 200; },
      container: recordingsTab,
      enabled: function (s) { return s.TabMode === 'replace' || s.TabMode === 'add'; },
      title: function (s) { return s.Title || 'Recorded Shows'; },
      place: placeOnTab
    }
  ];

  // Live TV's Recordings tab (React): #liveTvPage holds its sections directly, once they
  // have loaded ("Recently Added Recordings", "All Recordings").
  function recordingsTab() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/livetv') !== 0) {
      return null;
    }
    var params = new URLSearchParams(hash.indexOf('?') === -1 ? '' : hash.slice(hash.indexOf('?') + 1));
    if (params.get('tab') !== '3') {
      return null;
    }
    var page = document.querySelector('#liveTvPage:not(.hide)');
    if (!page) {
      return null;
    }
    var loaded = [].slice.call(page.children).some(function (c) {
      return /^(Recently Added Recordings|All Recordings)$/.test(heading(c));
    });
    return loaded ? page : null;
  }

  function nativeLatestRecordings(page) {
    return [].slice.call(page.children).filter(function (c) {
      return heading(c) === 'Recently Added Recordings';
    })[0];
  }

  function placeOnTab(page, section, s) {
    var latest = nativeLatestRecordings(page);
    var first = [].slice.call(page.children).filter(function (c) {
      return c.classList.contains('verticalSection');
    })[0];
    page.insertBefore(section, latest || first || null);
    applyTabMode(page, s);
  }

  // Hide (never remove: React owns it) Jellyfin's own row in "replace" mode.
  function applyTabMode(page, s) {
    var latest = nativeLatestRecordings(page);
    if (latest) {
      latest.style.display = s && s.TabMode === 'replace' && page.querySelector('.' + SECTION_CLASS) ? 'none' : '';
    }
  }

  // Home: where the row goes, per the plugin's "position" setting.
  function placeOnHome(container, section, s) {
    var rows = [].slice.call(container.children).filter(function (r) {
      return !r.classList.contains(SECTION_CLASS);
    });
    function find(test) {
      return rows.filter(function (r) { return test(heading(r)); })[0];
    }
    var latest = find(function (h) { return /^(Recently Added in|Latest)\b/i.test(h); });
    var anchor = null;
    var after = false;
    switch (s.Position) {
      case 'top':
        anchor = container.firstElementChild;
        break;
      case 'after-continue-watching':
        anchor = find(function (h) { return /^Continue Watching$/i.test(h); });
        after = true;
        break;
      case 'after-next-up':
        anchor = find(function (h) { return /^Next Up$/i.test(h); });
        after = true;
        break;
      case 'bottom':
        break;
      default:
        // "Recently Added in …" rows sit in one wrapper section; go ahead of it.
        anchor = latest && (latest.closest('.homeSectionsContainer > *') || latest);
    }
    if (!anchor) {
      container.appendChild(section);
    } else if (after) {
      container.insertBefore(section, anchor.nextSibling);
    } else {
      container.insertBefore(section, anchor);
    }
  }

  function el(tag, className, text) {
    var e = document.createElement(tag);
    if (className) {
      e.className = className;
    }
    if (text !== undefined && text !== null) {
      e.textContent = text;
    }
    return e;
  }

  function imageUrl(id) {
    return window.ApiClient.getImageUrl(id, { type: 'Primary', fillHeight: 446, fillWidth: 297, quality: 96 });
  }

  function buildCard(card) {
    var root = el('div', 'card overflowPortraitCard card-hoverable card-withuserdata');
    root.setAttribute('data-type', card.Type);
    root.setAttribute('data-id', card.Id);
    var box = el('div', 'cardBox cardBox-bottompadded');
    var scalable = el('div', 'cardScalable');
    scalable.appendChild(el('div', 'cardPadder cardPadder-overflowPortrait'));

    var image = el('a', 'cardImageContainer coveredImage cardContent');
    image.href = '#/details?id=' + card.Id;
    image.setAttribute('aria-label', card.Name);
    image.style.backgroundImage = 'url("' + imageUrl(card.ImageId) + '")';

    var indicators = el('div', 'cardIndicators');
    if (card.UnplayedCount > 0 && card.Count > 1) {
      indicators.appendChild(el('div', 'countIndicator indicator', String(card.UnplayedCount)));
    } else if (card.UnplayedCount === 0) {
      var played = el('div', 'playedIndicator indicator');
      played.appendChild(el('span', 'material-icons indicatorIcon check'));
      indicators.appendChild(played);
    }
    image.appendChild(indicators);

    if (card.ArchivedCount > 0) {
      var chip = el('div', 'episodeHistoryArchivedChip');
      chip.title = card.ArchivedCount === card.Count ? 'Archived' : card.ArchivedCount + ' of ' + card.Count + ' archived';
      chip.appendChild(el('span', 'material-icons inventory_2'));
      if (card.ArchivedCount !== card.Count) {
        chip.appendChild(el('span', null, String(card.ArchivedCount)));
      }
      image.appendChild(chip);
    }

    scalable.appendChild(image);
    box.appendChild(scalable);

    var footer = el('div', 'cardFooter');
    var first = el('div', 'cardText cardTextCentered cardText-first');
    first.appendChild(el('bdi', null, card.Name));
    footer.appendChild(first);
    var second = el('div', 'cardText cardTextCentered cardText-secondary');
    second.appendChild(el('bdi', null, card.Subtitle || ' '));
    footer.appendChild(second);
    box.appendChild(footer);
    root.appendChild(box);
    return root;
  }

  function buildSection(view, title, limit) {
    var section = el('div', 'verticalSection emby-scroller-container ' + SECTION_CLASS + ' ' + SECTION_CLASS + '-' + view.layout);
    section.setAttribute('data-eh-view', view.name);
    section.setAttribute('data-eh-limit', limit || '');
    var header = el('div', 'sectionTitleContainer sectionTitleContainer-cards padded-left');
    header.appendChild(el('h2', 'sectionTitle sectionTitle-cards', title));
    var filters = el('div', 'episodeHistoryRecordingsFilters');
    FILTERS.forEach(function (f) {
      var b = el('button', 'episodeHistoryRecordingsFilter', f.label);
      b.type = 'button';
      b.setAttribute('data-filter', f.id);
      b.addEventListener('click', function () {
        saveFilter(f.id);
        render(section, f.id);
      });
      filters.appendChild(b);
    });
    header.appendChild(filters);
    section.appendChild(header);

    var scroller = el('div', 'padded-top-focusscale padded-bottom-focusscale episodeHistoryRecordingsScroller');
    // Not `.itemsContainer`: jellyfin-web's home tab calls pause() on every element with that
    // class when leaving the page (no check that it exists), which only its own
    // emby-itemscontainer has. Seen live: "t.value.pause is not a function" crashed the page.
    var items = el('div', 'focuscontainer-x episodeHistoryRecordingsItems');
    scroller.appendChild(items);
    section.appendChild(scroller);
    return section;
  }

  function fill(section, data, filter) {
    [].slice.call(section.querySelectorAll('.episodeHistoryRecordingsFilter')).forEach(function (b) {
      b.classList.toggle('episodeHistoryRecordingsFilter-active', b.getAttribute('data-filter') === filter);
    });
    var items = section.querySelector('.episodeHistoryRecordingsItems');
    items.innerHTML = '';
    var cards = (data && data.Cards) || [];
    if (!cards.length) {
      var label = filter === 'archived' ? 'No archived recordings yet.'
        : filter === 'current' ? 'Nothing in the recordings folders.' : 'No recordings yet.';
      items.appendChild(el('div', 'episodeHistoryRecordingsEmpty', label));
      return;
    }
    cards.forEach(function (c) {
      items.appendChild(buildCard(c));
    });
  }

  function sectionLimit(section) {
    return parseInt(section.getAttribute('data-eh-limit'), 10) || null;
  }

  function render(section, filter) {
    var limit = sectionLimit(section);
    var cached = cache[key(filter, limit)];
    if (cached) {
      fill(section, cached.data, filter);
    }
    if (!cached || Date.now() - cached.at > MAX_AGE_MS) {
      fetchCards(filter, limit).then(function (data) {
        if (section.isConnected && currentFilter() === filter) {
          fill(section, data, filter);
        }
      }).catch(function () { log('loading recordings failed'); });
    }
  }

  function addStyles() {
    if (document.getElementById('episodeHistoryRecordingsStyle')) {
      return;
    }
    var style = el('style');
    style.id = 'episodeHistoryRecordingsStyle';
    style.textContent = [
      '.' + SECTION_CLASS + ' .sectionTitleContainer { display: flex; align-items: center; gap: 1em; flex-wrap: wrap; }',
      '.episodeHistoryRecordingsFilters { display: inline-flex; gap: .4em; }',
      '.episodeHistoryRecordingsFilter { background: rgba(255,255,255,.08); color: inherit; border: 0; border-radius: 1em;',
      '  padding: .25em .9em; font: inherit; font-size: .85em; cursor: pointer; }',
      '.episodeHistoryRecordingsFilter:hover { background: rgba(255,255,255,.16); }',
      '.episodeHistoryRecordingsFilter-active { background: #00a4dc; color: #fff; }',
      '.' + SECTION_CLASS + '-row .episodeHistoryRecordingsScroller { overflow-x: auto; overflow-y: hidden; scrollbar-width: thin; }',
      '.episodeHistoryRecordingsItems { display: flex; flex-wrap: nowrap; padding-left: 3.3%; }',
      '.' + SECTION_CLASS + '-grid .episodeHistoryRecordingsItems { flex-wrap: wrap; padding-right: 3.3%; }',
      '.episodeHistoryRecordingsItems > .card { flex: 0 0 auto; }',
      '.episodeHistoryRecordingsEmpty { opacity: .7; padding: 1em 0; }',
      '.episodeHistoryArchivedChip { position: absolute; left: .4em; bottom: .4em; display: flex; align-items: center; gap: .15em;',
      '  background: rgba(0,0,0,.7); color: #fff; border-radius: .3em; padding: .1em .35em; font-size: .8em; }',
      '.episodeHistoryArchivedChip .material-icons { font-size: 1.2em; }'
    ].join('\n');
    document.head.appendChild(style);
  }

  // A tab section left behind when switching to another Live TV tab (same React page).
  function removeStrayTabSections() {
    if (recordingsTab()) {
      return;
    }
    [].slice.call(document.querySelectorAll('.' + SECTION_CLASS + '[data-eh-view="tab"]')).forEach(function (s) {
      var page = s.parentNode;
      s.remove();
      if (page) {
        applyTabMode(page, settings);
      }
    });
  }

  function checkView(view) {
    var container = view.container();
    if (!container || loading[view.name]) {
      return;
    }
    // Already shown (the page is kept around): refresh it when stale.
    var existing = container.querySelector('.' + SECTION_CLASS + '[data-eh-view="' + view.name + '"]');
    if (existing) {
      var shown = cache[key(currentFilter(), sectionLimit(existing))];
      if (!shown || Date.now() - shown.at > MAX_AGE_MS) {
        render(existing, currentFilter());
      }
      if (view.name === 'tab') {
        applyTabMode(container, settings);
      }
      return;
    }
    var filter = currentFilter();
    var limit = settings ? view.limit(settings) : null;
    var cached = cache[key(filter, limit)];
    if (cached && !cached.data && Date.now() - cached.at < MAX_AGE_MS) {
      return; // failed recently
    }
    loading[view.name] = true;
    var ready = cached && cached.data ? Promise.resolve(cached.data) : fetchCards(filter, limit);
    ready.then(function (data) {
      loading[view.name] = false;
      var s = (data && data.Settings) || {};
      if (!view.enabled(s) || !container.isConnected
        || container.querySelector('.' + SECTION_CLASS + '[data-eh-view="' + view.name + '"]')) {
        return;
      }
      if (!storedFilter() && s.DefaultFilter) {
        filter = s.DefaultFilter;
      }
      addStyles();
      var section = buildSection(view, view.title(s), view.limit(s));
      view.place(container, section, s);
      render(section, filter);
    }).catch(function () {
      loading[view.name] = false;
      log('loading recordings failed');
    });
  }

  function check() {
    removeStrayTabSections();
    VIEWS.forEach(checkView);
  }

  var scheduled = false;
  function scheduleCheck() {
    if (!scheduled) {
      scheduled = true;
      setTimeout(function () {
        scheduled = false;
        if (window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()) {
          check();
        }
      }, 100);
    }
  }

  new MutationObserver(scheduleCheck).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', scheduleCheck);
  scheduleCheck();

  log('recordings row script loaded');
})();
