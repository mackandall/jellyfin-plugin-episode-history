/*
 * Episode History — the home screen's recordings row (SageTV's Recordings view, Channels
 * DVR's Recordings section): what the DVR recorded, newest first, one card per show, with
 * an All / Current / Archived filter. "Archived" recordings are ones since moved out of the
 * recordings folders (e.g. by JellyDVR Space); they stay in this row, as in SageTV.
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

  var cache = {};        // filter -> { at, data }
  var loading = false;
  var defaultFilter = null;

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
    return storedFilter() || defaultFilter || 'all';
  }

  var inflight = {};     // filter -> pending request
  function fetchRow(filter) {
    if (inflight[filter]) {
      return inflight[filter];
    }
    var apiClient = window.ApiClient;
    inflight[filter] = apiClient.ajax({
      type: 'GET',
      url: apiClient.getUrl('EpisodeHistory/Recordings', { filter: filter }),
      dataType: 'json'
    }).then(function (data) {
      delete inflight[filter];
      defaultFilter = data && data.Settings && data.Settings.DefaultFilter;
      cache[filter] = { at: Date.now(), data: data };
      return data;
    }, function (err) {
      delete inflight[filter];
      // Don't retry on every DOM change after a failure.
      cache[filter] = cache[filter] || { at: Date.now(), data: null };
      cache[filter].at = Date.now();
      throw err;
    });
    return inflight[filter];
  }

  function homeContainer() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/home') !== 0) {
      return null;
    }
    var container = document.querySelector('.page:not(.hide) .homeSectionsContainer');
    return container && container.offsetParent !== null ? container : null;
  }

  function heading(section) {
    var h = section.querySelector('h2');
    return h ? h.textContent.trim() : '';
  }

  // Where the row goes, per the plugin's "position" setting.
  function place(container, section, position) {
    var rows = [].slice.call(container.children).filter(function (s) {
      return !s.classList.contains(SECTION_CLASS);
    });
    function find(test) {
      return rows.filter(function (s) { return test(heading(s)); })[0];
    }
    var latest = find(function (h) { return /^(Recently Added in|Latest)\b/i.test(h); });
    var anchor = null;
    var after = false;
    switch (position) {
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

  function buildSection(title) {
    var section = el('div', 'verticalSection emby-scroller-container ' + SECTION_CLASS);
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

  function render(section, filter) {
    var cached = cache[filter];
    if (cached) {
      fill(section, cached.data, filter);
    }
    if (!cached || Date.now() - cached.at > MAX_AGE_MS) {
      fetchRow(filter).then(function (data) {
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
      '.episodeHistoryRecordingsScroller { overflow-x: auto; overflow-y: hidden; scrollbar-width: thin; }',
      '.episodeHistoryRecordingsItems { display: flex; flex-wrap: nowrap; padding-left: 3.3%; }',
      '.episodeHistoryRecordingsItems > .card { flex: 0 0 auto; }',
      '.episodeHistoryRecordingsEmpty { opacity: .7; padding: 1em 0; }',
      '.episodeHistoryArchivedChip { position: absolute; left: .4em; bottom: .4em; display: flex; align-items: center; gap: .15em;',
      '  background: rgba(0,0,0,.7); color: #fff; border-radius: .3em; padding: .1em .35em; font-size: .8em; }',
      '.episodeHistoryArchivedChip .material-icons { font-size: 1.2em; }'
    ].join('\n');
    document.head.appendChild(style);
  }

  function check() {
    var container = homeContainer();
    if (!container || loading) {
      return;
    }
    // Already shown (jellyfin-web keeps the home page around): refresh it when stale.
    var existing = container.querySelector('.' + SECTION_CLASS);
    if (existing) {
      var shown = cache[currentFilter()];
      if (!shown || Date.now() - shown.at > MAX_AGE_MS) {
        render(existing, currentFilter());
      }
      return;
    }
    // Wait for jellyfin-web's own rows, so ours can be placed among them.
    if (!container.querySelector('.sectionTitle')) {
      return;
    }
    var filter = currentFilter();
    var cached = cache[filter];
    if (cached && !cached.data && Date.now() - cached.at < MAX_AGE_MS) {
      return; // failed recently
    }
    loading = true;
    var ready = cached && cached.data ? Promise.resolve(cached.data) : fetchRow(filter);
    ready.then(function (data) {
      loading = false;
      var settings = (data && data.Settings) || {};
      if (!settings.Enabled || container.querySelector('.' + SECTION_CLASS) || !container.isConnected) {
        return;
      }
      if (!storedFilter() && settings.DefaultFilter && settings.DefaultFilter !== filter) {
        filter = settings.DefaultFilter;
      }
      addStyles();
      var section = buildSection(settings.Title || 'Recorded Shows');
      place(container, section, settings.Position);
      render(section, filter);
    }).catch(function () {
      loading = false;
      log('loading recordings failed');
    });
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
