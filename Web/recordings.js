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
    // A file that doesn't count as recorded (cut short, failed stub): marked so it's easy to delete.
    if (c.partial > 0) {
      var single = c.count === 1;
      var text = single ? 'Partial' + (c.partialMinutes ? ' · ' + c.partialMinutes + ' min' : '') : c.partial + ' partial';
      html += '<div class="episodeHistoryPartialChip" title="Partial recording: too short to count as recorded">'
        + '<span class="material-icons timelapse" aria-hidden="true"></span><span>' + esc(text) + '</span></div>';
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
        count: c.Count, unplayed: c.UnplayedCount, played: c.UnplayedCount === 0, archived: c.ArchivedCount,
        partial: c.PartialCount || 0, partialMinutes: c.PartialMinutes
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
      '.episodeHistoryPartialChip { position: absolute; right: .4em; bottom: .4em; display: flex; align-items: center; gap: .15em;',
      '  background: #f59e0b; color: #1a1a1a; font-weight: 600; border-radius: .3em; padding: .1em .35em; font-size: .8em; }',
      '.episodeHistoryPartialChip .material-icons { font-size: 1.2em; }',
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
        count: 1, unplayed: r.Played ? 0 : 1, played: r.Played, archived: r.Archived ? 1 : 0,
        partial: r.Partial ? 1 : 0, partialMinutes: r.Minutes
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

  /*
   * Card buttons when jellyfin-web doesn't handle them. Its card clicks (play, watched, "...")
   * come from the emby-itemscontainer element, but jellyfin-web 12 draws Live TV > Recordings with
   * React and never loads that element, so on that page the container stays a plain div and the
   * buttons did nothing. There (only where the container wasn't upgraded) they're handled here,
   * with a small menu of our own: Play, Open, Mark watched, Delete.
   */
  var MENU_ID = 'episodeHistoryCardMenu';

  function handledByJellyfin(container) {
    return !!container && typeof container.pause === 'function';
  }

  function ownSession() {
    var client = window.ApiClient;
    return client.getSessions({ deviceId: client.deviceId() }).then(function (sessions) {
      return (sessions || []).filter(function (x) { return x.DeviceId === client.deviceId(); })[0] || null;
    });
  }

  function playItem(id) {
    var client = window.ApiClient;
    return ownSession().then(function (session) {
      if (session) {
        return client.ajax({ type: 'POST', url: client.getUrl('Sessions/' + session.Id + '/Playing', { playCommand: 'PlayNow', itemIds: id }) });
      }
    });
  }

  function setPlayed(card, played) {
    var client = window.ApiClient;
    var id = card.getAttribute('data-id');
    var call = played ? client.markPlayed(client.getCurrentUserId(), id, new Date()) : client.markUnplayed(client.getCurrentUserId(), id);
    return Promise.resolve(call).then(function () {
      var button = card.querySelector('[is="emby-playstatebutton"]');
      if (button) {
        button.setAttribute('data-played', played ? 'true' : 'false');
        button.classList.toggle('playstatebutton-played', played);
      }
      var indicators = card.querySelector('.cardIndicators');
      if (indicators && !indicators.querySelector('.countIndicator')) {
        indicators.innerHTML = played ? '<div class="playedIndicator indicator"><span class="material-icons indicatorIcon check" aria-hidden="true"></span></div>' : '';
      }
    });
  }

  function closeMenu() {
    var m = document.getElementById(MENU_ID);
    if (m) {
      m.parentNode.removeChild(m);
    }
    document.removeEventListener('keydown', onMenuKey, true);
  }

  function onMenuKey(e) {
    if (e.key === 'Escape' || e.key === 'Backspace' || e.keyCode === 10009 || e.keyCode === 461) {
      e.preventDefault();
      e.stopPropagation();
      closeMenu();
    }
  }

  function menuItem(icon, label, onClick) {
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'listItem listItem-button actionSheetMenuItem emby-button';
    b.style.cssText = 'display:flex;align-items:center;gap:.8em;width:100%;padding:.7em 1em;background:none;border:0;color:inherit;font-size:1em;cursor:pointer;text-align:left;';
    b.innerHTML = '<span class="material-icons ' + icon + '" aria-hidden="true"></span><span class="eh-menu-text"></span>';
    b.querySelector('.eh-menu-text').textContent = label;
    b.addEventListener('click', onClick);
    return b;
  }

  function openMenu(card, anchor) {
    closeMenu();
    var id = card.getAttribute('data-id');
    var type = card.getAttribute('data-type');
    var played = (card.querySelector('[is="emby-playstatebutton"]') || card).getAttribute('data-played') === 'true';
    var single = type !== 'Series';
    var name = (card.querySelector('.cardText-first') || {}).textContent || '';
    var sub = (card.querySelector('.cardText-secondary') || {}).textContent || '';

    var overlay = el('div');
    overlay.id = MENU_ID;
    overlay.style.cssText = 'position:fixed;inset:0;z-index:100000;';
    var box = el('div');
    box.style.cssText = 'position:fixed;min-width:15em;max-width:22em;background:#252525;color:#fff;border-radius:.4em;box-shadow:0 .3em 1.5em rgba(0,0,0,.6);padding:.3em 0;';
    var title = el('div', null, name + (sub && single ? ' · ' + sub : ''));
    title.style.cssText = 'padding:.5em 1em;opacity:.7;font-size:.9em;border-bottom:1px solid rgba(255,255,255,.1);';
    box.appendChild(title);

    box.appendChild(menuItem('play_arrow', 'Play', function () { closeMenu(); playItem(id); }));
    box.appendChild(menuItem('info', 'Open', function () { closeMenu(); window.location.hash = '#/details?id=' + id; }));
    box.appendChild(menuItem(played ? 'remove_done' : 'done', played ? 'Mark as unwatched' : 'Mark as watched', function () {
      closeMenu();
      setPlayed(card, !played);
    }));

    var client = window.ApiClient;
    if (!single) {
      showDeleteEntry(id, name, function (label, onClick) {
        if (box.isConnected) {
          box.appendChild(menuItem('delete_sweep', label, function () { closeMenu(); onClick(); }));
        }
      }, function () {
        card.parentNode && card.parentNode.removeChild(card);
      });
    }
    Promise.resolve(single ? client.getCurrentUser() : null).then(function (user) {
      var policy = user && user.Policy;
      if (!policy || !(policy.EnableContentDeletion || (policy.EnableContentDeletionFromFolders || []).length) || !box.isConnected) {
        return;
      }
      // Two taps instead of a confirm() dialog, which TV remotes handle poorly.
      var armed = false;
      var del = menuItem('delete', 'Delete recording', function () {
        var text = del.querySelector('.eh-menu-text');
        if (!armed) {
          armed = true;
          text.textContent = 'Press again to delete for good';
          del.style.color = '#ff6b6b';
          return;
        }
        text.textContent = 'Deleting…';
        del.disabled = true;
        var started = Date.now();
        client.deleteItem(id).then(function () {
          log('deleted ' + id + ' in ' + (Date.now() - started) + ' ms');
          closeMenu();
          card.parentNode && card.parentNode.removeChild(card);
        }, function () {
          text.textContent = 'Could not delete it';
        });
      });
      box.appendChild(del);
    });

    overlay.appendChild(box);
    overlay.addEventListener('click', function (e) {
      if (e.target === overlay) {
        closeMenu();
      }
    });
    document.body.appendChild(overlay);
    var r = anchor.getBoundingClientRect();
    var left = Math.min(r.left, window.innerWidth - 260);
    var top = r.bottom + 4;
    box.style.left = Math.max(8, left) + 'px';
    box.style.top = Math.max(8, Math.min(top, window.innerHeight - 260)) + 'px';
    document.addEventListener('keydown', onMenuKey, true);
    var first = box.querySelector('button');
    if (first) {
      first.focus();
    }
  }

  /*
   * "Delete all recordings…" for a show: in our own card menu above and in jellyfin-web's "..."
   * action sheet (a show's card or its page). Offered when the caller may delete at least one of
   * the show's recordings still in the recordings folders (a dry run says how many); archived
   * copies are kept. A window asks first, with the count and size.
   */
  function seriesDeleteUrl(id, params) {
    return window.ApiClient.getUrl('EpisodeHistory/Recordings/Series/' + id, params);
  }

  function dryRun(id) {
    var client = window.ApiClient;
    return client.ajax({ type: 'DELETE', url: seriesDeleteUrl(id, { dryRun: true }), dataType: 'json' });
  }

  function gbText(bytes) {
    return (bytes / 1073741824).toFixed(bytes >= 100 * 1073741824 ? 0 : 1) + ' GB';
  }

  function notify(message) {
    if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
      window.Dashboard.alert(message);
    } else {
      log(message);
    }
  }

  // Calls add(label, onClick) when there's something the caller may delete.
  function showDeleteEntry(id, name, add, onDeleted) {
    dryRun(id).then(function (r) {
      if (r && r.Count > 0) {
        add('Delete all ' + r.Count + ' recording' + (r.Count === 1 ? '' : 's') + '\u2026', function () {
          // Ask again: something may have finished or been deleted meanwhile.
          setTimeout(function () {
            dryRun(id).then(function (fresh) { confirmDelete(id, name, fresh, onDeleted); });
          }, 150);
        });
      }
    }).catch(function () {});
  }

  function confirmDelete(id, name, r, onDeleted) {
    var overlay = el('div');
    overlay.style.cssText = 'position:fixed;inset:0;z-index:100000;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;padding:16px;';
    var box = el('div');
    box.style.cssText = 'background:#202020;color:#fff;border-radius:10px;max-width:480px;width:100%;padding:1.2em 1.4em;box-shadow:0 8px 40px rgba(0,0,0,.6);';
    overlay.appendChild(box);
    function close() {
      document.removeEventListener('keydown', onKey, true);
      overlay.parentNode && overlay.parentNode.removeChild(overlay);
    }
    function onKey(e) {
      if (e.key === 'Escape' || e.key === 'Backspace' || e.keyCode === 10009 || e.keyCode === 461) {
        e.preventDefault();
        e.stopPropagation();
        close();
      }
    }
    var h = el('h2', null, 'Delete all recordings?');
    h.style.cssText = 'margin:0 0 .5em;font-size:1.3em;font-weight:500;';
    box.appendChild(h);
    var p = el('div', null, name + ': ' + r.Count + ' recording' + (r.Count === 1 ? '' : 's') + ' (' + gbText(r.Bytes) + ') in the recordings folders will be deleted from the disk. This can\'t be undone.');
    p.style.cssText = 'margin-bottom:.6em;line-height:1.4;';
    box.appendChild(p);
    var kept = el('div', null, 'Archived copies are kept, and the episodes stay in the history as recorded.'
      + (r.Skipped ? ' ' + r.Skipped + ' can\'t be deleted now (recording, or not yours to delete) and are left.' : ''));
    kept.style.cssText = 'opacity:.75;font-size:.9em;margin-bottom:1.1em;line-height:1.4;';
    box.appendChild(kept);

    var row = el('div');
    row.style.cssText = 'display:flex;gap:.8em;justify-content:flex-end;';
    var cancel = el('button', null, 'Cancel');
    cancel.type = 'button';
    cancel.style.cssText = 'padding:.55em 1.2em;border-radius:6px;border:0;background:rgba(255,255,255,.12);color:inherit;font:inherit;cursor:pointer;';
    cancel.addEventListener('click', close);
    var go = el('button', null, 'Delete ' + r.Count);
    go.type = 'button';
    go.style.cssText = 'padding:.55em 1.2em;border-radius:6px;border:0;background:#c62828;color:#fff;font:inherit;cursor:pointer;';
    go.addEventListener('click', function () {
      go.disabled = cancel.disabled = true;
      go.textContent = 'Deleting\u2026';
      var client = window.ApiClient;
      client.ajax({ type: 'DELETE', url: seriesDeleteUrl(id), dataType: 'json' }).then(function (done) {
        close();
        cache = {};
        notify('Deleted ' + done.Deleted + ' recording' + (done.Deleted === 1 ? '' : 's') + ' of ' + name + '.'
          + (done.Skipped ? ' ' + done.Skipped + ' left (recording now, or not yours to delete).' : ''));
        if (onDeleted) {
          onDeleted();
        }
        scheduleCheck();
      }, function () {
        close();
        notify('Deleting the recordings failed.');
      });
    });
    row.appendChild(cancel);
    row.appendChild(go);
    box.appendChild(row);
    overlay.addEventListener('click', function (e) {
      if (e.target === overlay) {
        close();
      }
    });
    document.body.appendChild(overlay);
    document.addEventListener('keydown', onKey, true);
    cancel.focus();
  }

  // jellyfin-web's own "..." sheet: the show whose card's menu button (or right-click) opened
  // it, else the show page's "..." button. The same detection as JellyDVR Space's archive.js.
  var sheetItemId = null;
  var sheetClickedAt = 0;
  function rememberSheetTarget(e, isContextMenu) {
    var t = e.target;
    if (!t || !t.closest) {
      return;
    }
    var holder = t.closest('.card[data-id], .listItem[data-id]');
    if (holder && holder.classList.contains('actionSheetMenuItem')) {
      return;
    }
    if (holder && (isContextMenu || t.closest('[data-action="menu"]'))) {
      sheetItemId = holder.getAttribute('data-id');
      sheetClickedAt = Date.now();
    } else if (t.closest('.btnMoreCommands')) {
      sheetItemId = null;
      sheetClickedAt = Date.now();
    }
  }
  document.addEventListener('click', function (e) { rememberSheetTarget(e, false); }, true);
  document.addEventListener('contextmenu', function (e) { rememberSheetTarget(e, true); }, true);

  function sheetTargetId() {
    if (sheetItemId && Date.now() - sheetClickedAt < 5000) {
      return sheetItemId;
    }
    var hash = window.location.hash || '';
    var q = hash.indexOf('?');
    return hash.indexOf('#/details') === 0 && q !== -1 ? new URLSearchParams(hash.slice(q + 1)).get('id') : null;
  }

  function injectIntoSheet(scroller) {
    var id = sheetTargetId();
    var client = window.ApiClient;
    if (!id || !client) {
      return;
    }
    client.getItem(client.getCurrentUserId(), id).then(function (item) {
      if (!item || item.Type !== 'Series') {
        return;
      }
      showDeleteEntry(id, item.Name, function (label, onClick) {
        if (!scroller.isConnected || scroller.querySelector('[data-id="episodehistory-deleteall"]')) {
          return;
        }
        var b = document.createElement('button');
        b.setAttribute('is', 'emby-button');
        b.type = 'button';
        b.className = 'listItem listItem-button actionSheetMenuItem emby-button';
        b.setAttribute('data-id', 'episodehistory-deleteall');
        b.innerHTML = '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons delete_sweep" aria-hidden="true"></span>'
          + '<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText"></div></div>';
        b.querySelector('.actionSheetItemText').textContent = label;
        // No stopPropagation: the click bubbling up is what closes the sheet.
        b.addEventListener('click', onClick);
        scroller.appendChild(b);
      });
    }).catch(function () {});
  }

  new MutationObserver(function (mutations) {
    mutations.forEach(function (m) {
      m.addedNodes.forEach(function (node) {
        if (node.nodeType !== 1) {
          return;
        }
        var scroller = node.classList && node.classList.contains('actionSheetScroller')
          ? node
          : (node.querySelector && node.querySelector('.actionSheetScroller'));
        if (scroller) {
          injectIntoSheet(scroller);
        }
      });
    });
  }).observe(document.body, { childList: true, subtree: true });

  document.addEventListener('click', function (e) {
    var button = e.target && e.target.closest && e.target.closest('.episodeHistoryRecordingsItems .cardOverlayButton');
    if (!button) {
      return;
    }
    var container = button.closest('.episodeHistoryRecordingsItems');
    var card = button.closest('.card');
    if (!card || handledByJellyfin(container)) {
      return;
    }
    e.preventDefault();
    e.stopPropagation();
    var action = button.getAttribute('data-action');
    if (action === 'menu') {
      openMenu(card, button);
    } else if (action === 'resume') {
      playItem(card.getAttribute('data-id'));
    } else if (button.getAttribute('is') === 'emby-playstatebutton') {
      setPlayed(card, button.getAttribute('data-played') !== 'true');
    }
  }, true);

  new MutationObserver(scheduleCheck).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', scheduleCheck);
  scheduleCheck();

  log('recordings row script loaded');
})();
