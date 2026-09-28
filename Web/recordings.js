/*
 * Episode History — recordings views (SageTV's Recordings view, Channels DVR's Recordings
 * section): what the DVR recorded, newest first, one card per show, with an All / Current /
 * Archived filter. "Archived" recordings are ones since moved out of the recordings folders
 * (e.g. by JellyDVR Space); they stay listed, as in SageTV. Shown as
 *   - a row on the home screen,
 *   - a "Recordings" row above Next Up on a show's page (that show's recordings, one card
 *     each), and
 *   - a "Recordings" row above Next Up on a show's page (that show's recordings, one card each),
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

  function esc(text) {
    return String(text === undefined || text === null ? '' : text)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  /*
   * One card, in jellyfin-web's own card markup, so that inside an emby-itemscontainer its
   * clicks are handled by jellyfin-web itself (itemShortcuts): the image opens the item, and
   * the hover overlay has Play/Resume, the played toggle and the "..." menu, exactly like
   * native cards. Built as markup because jellyfin-web's custom elements (emby-itemscontainer,
   * paper-icon-button-light, emby-playstatebutton) are polyfilled and only upgrade that way.
   */
  function cardHtml(c) {
    var backdrop = c.shape === 'backdrop';
    var html = '<div class="card ' + (backdrop ? 'overflowBackdropCard' : 'overflowPortraitCard') + ' card-hoverable card-withuserdata"'
      + ' data-isfolder="' + (c.type === 'Series') + '" data-serverid="' + esc(window.ApiClient.serverId()) + '"'
      + ' data-id="' + esc(c.id) + '" data-type="' + esc(c.type) + '" data-mediatype="' + (c.type === 'Series' ? '' : 'Video') + '">'
      + '<div class="cardBox cardBox-bottompadded"><div class="cardScalable">'
      + '<div class="cardPadder ' + (backdrop ? 'cardPadder-overflowBackdrop' : 'cardPadder-overflowPortrait') + '"></div>'
      + '<a data-action="link" class="cardImageContainer coveredImage cardContent itemAction" href="#/details?id=' + esc(c.id) + '"'
      + ' aria-label="' + esc(c.name) + '" style="background-image:url(&quot;' + esc(c.image) + '&quot;)">'
      + '<div class="cardIndicators">';
    if (c.unplayed > 0 && c.count > 1) {
      html += '<div class="countIndicator indicator">' + c.unplayed + '</div>';
    } else if (c.played) {
      html += '<div class="playedIndicator indicator"><span class="material-icons indicatorIcon check" aria-hidden="true"></span></div>';
    }
    html += '</div>';
    if (c.archived > 0) {
      var all = c.archived === c.count;
      html += '<div class="episodeHistoryArchivedChip" title="' + (all ? 'Archived' : c.archived + ' of ' + c.count + ' archived') + '">'
        + '<span class="material-icons inventory_2" aria-hidden="true"></span><span>' + (all ? 'Archived' : c.archived) + '</span></div>';
    }
    html += '</a>'
      + '<div class="cardOverlayContainer itemAction" data-action="link">'
      + '<button is="paper-icon-button-light" type="button" title="Play" class="cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light cardOverlayFab-primary" data-action="resume">'
      + '<span class="material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover play_arrow" aria-hidden="true"></span></button>'
      + '<div class="cardOverlayButton-br flex">'
      + '<button is="emby-playstatebutton" type="button" data-action="none" class="cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light emby-button"'
      + ' data-id="' + esc(c.id) + '" data-serverid="' + esc(window.ApiClient.serverId()) + '" data-itemtype="' + esc(c.type) + '" data-played="' + !!c.played + '">'
      + '<span class="material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover check" aria-hidden="true"></span></button>'
      + '<button is="paper-icon-button-light" type="button" title="More" class="cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light" data-action="menu">'
      + '<span class="material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover more_vert" aria-hidden="true"></span></button>'
      + '</div></div>'
      + '</div>'
      + '<div class="cardFooter"><div class="cardText cardTextCentered cardText-first"><bdi>' + esc(c.name) + '</bdi></div>'
      + '<div class="cardText cardTextCentered cardText-secondary"><bdi>' + (esc(c.subtitle) || '&nbsp;') + '</bdi></div></div>'
      + '</div></div>';
    return html;
  }

  // A real (polyfilled) emby-itemscontainer: jellyfin-web handles its cards' clicks, and the
  // home tab's pause()/resume() on every .itemsContainer finds the methods it expects. Seen
  // live: a plain div with that class crashed the home tab ("t.value.pause is not a function").
  function itemsContainerInto(scroller) {
    scroller.innerHTML = '<div is="emby-itemscontainer" class="itemsContainer focuscontainer-x episodeHistoryRecordingsItems"></div>';
    return scroller.firstElementChild;
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
    itemsContainerInto(scroller);
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
    items.innerHTML = cards.map(function (c) {
      return cardHtml({
        id: c.Id, type: c.Type, name: c.Name, subtitle: c.Subtitle, image: imageUrl(c.ImageId),
        count: c.Count, unplayed: c.UnplayedCount, played: c.UnplayedCount === 0, archived: c.ArchivedCount
      });
    }).join('');
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
      '.episodeHistoryArchivedChip .material-icons { font-size: 1.2em; }',
      '.' + SHOW_CLASS + ' .episodeHistoryRecordingsItems { padding-left: 0; }'
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

  /*
   * A show's page: a "Recordings" row above Next Up with that show's DVR recordings, newest
   * first, one card per recording (archived ones marked). The details page is reused for the
   * next item, so the row carries the show's id and is dropped on any other page. Items that
   * aren't shows get a 404 from the plugin, remembered so they aren't asked about again.
   */
  var SHOW_CLASS = 'episodeHistoryShowRecordings';
  var showCache = {};    // series id -> { at, data } (data null: not a show / failed)
  var showInflight = {};

  function detailsId() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/details') !== 0 || hash.indexOf('?') === -1) {
      return null;
    }
    return new URLSearchParams(hash.slice(hash.indexOf('?') + 1)).get('id');
  }

  function fetchShow(id) {
    if (showInflight[id]) {
      return showInflight[id];
    }
    var apiClient = window.ApiClient;
    showInflight[id] = apiClient.ajax({
      type: 'GET',
      url: apiClient.getUrl('EpisodeHistory/Recordings/Series/' + id),
      dataType: 'json'
    }).then(function (data) {
      delete showInflight[id];
      showCache[id] = { at: Date.now(), data: data };
      return data;
    }, function () {
      delete showInflight[id];
      showCache[id] = { at: Date.now(), data: null };
      return null;
    });
    return showInflight[id];
  }

  function recordedDate(utc) {
    var d = new Date(utc);
    return isNaN(d) ? '' : 'Recorded ' + d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  function buildShowSection(id, recordings) {
    var section = el('div', 'verticalSection detailVerticalSection emby-scroller-container ' + SHOW_CLASS + ' ' + SECTION_CLASS + '-row');
    section.setAttribute('data-eh-series', id);
    // No padded-left here: the details page's own sections line up without it.
    var header = el('div', 'sectionTitleContainer sectionTitleContainer-cards');
    header.appendChild(el('h2', 'sectionTitle sectionTitle-cards', 'Recordings'));
    section.appendChild(header);
    var scroller = el('div', 'padded-top-focusscale padded-bottom-focusscale episodeHistoryRecordingsScroller');
    var items = itemsContainerInto(scroller);
    items.innerHTML = recordings.map(function (r) {
      return cardHtml({
        id: r.Id, type: 'Episode', shape: 'backdrop', name: r.Name, subtitle: recordedDate(r.RecordedUtc),
        image: window.ApiClient.getImageUrl(r.Id, { type: 'Primary', fillWidth: 480, fillHeight: 270, quality: 96 }),
        count: 1, unplayed: r.Played ? 0 : 1, played: r.Played, archived: r.Archived ? 1 : 0
      });
    }).join('');
    section.appendChild(scroller);
    return section;
  }

  function checkShowPage() {
    var id = detailsId();
    [].slice.call(document.querySelectorAll('.' + SHOW_CLASS)).forEach(function (s) {
      if (s.getAttribute('data-eh-series') !== id || !s.closest('.page:not(.hide)')) {
        s.remove();
      }
    });
    if (!id) {
      return;
    }
    var page = document.querySelector('.page:not(.hide)');
    var nextUp = page && page.querySelector('.nextUpSection');
    if (!nextUp) {
      return;
    }
    var cached = showCache[id];
    var shown = page.querySelector('.' + SHOW_CLASS);
    if (!cached || Date.now() - cached.at > MAX_AGE_MS) {
      if (!showInflight[id]) {
        fetchShow(id).then(scheduleCheck);
      }
      if (!cached) {
        return;
      }
    }
    var data = cached.data;
    var recordings = data && data.Enabled && data.Recordings ? data.Recordings : [];
    var signature = recordings.map(function (r) { return r.Id + (r.Played ? 'p' : '') + (r.Archived ? 'a' : ''); }).join(',');
    if (shown && shown.getAttribute('data-eh-sig') === signature) {
      return;
    }
    if (shown) {
      shown.remove();
    }
    if (!recordings.length) {
      return;
    }
    addStyles();
    var section = buildShowSection(id, recordings);
    section.setAttribute('data-eh-sig', signature);
    nextUp.parentNode.insertBefore(section, nextUp);
  }

  function check() {
    removeStrayTabSections();
    VIEWS.forEach(checkView);
    checkShowPage();
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
