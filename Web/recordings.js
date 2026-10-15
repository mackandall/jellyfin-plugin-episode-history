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

  var VIEW_KEY = 'episodeHistory.recordingsView';
  var cache = {};        // filter|limit -> { at, data }
  var dateCache = {};    // filter -> { at, data }: the list by date
  var dateInflight = {};
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
    // Still being recorded.
    if (c.recording > 0) {
      html += '<div class="episodeHistoryRecordingChip" title="Recording now">'
        + '<span class="material-icons fiber_manual_record" aria-hidden="true"></span><span>Recording</span></div>';
    }
    // A file that doesn't count as recorded (cut short, failed stub): marked so it's easy to delete.
    // The tuner went quiet while it was being made: part of the programme is missing. Takes the
    // partial chip's place (it says more); the menu offers to keep it or record it again.
    if (c.stallText) {
      html += '<div class="episodeHistoryPartialChip episodeHistoryStalledChip" title="' + esc(c.stallTitle || 'The tuner went quiet during this recording') + '">'
        + '<span class="material-icons warning" aria-hidden="true"></span><span>' + esc(c.stallText) + '</span></div>';
    } else if (c.partial > 0) {
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
    if (view.name === 'tab') {
      // One switch: the cards are the usual view, so only "By date" needs a button (on/off).
      var views = el('div', 'episodeHistoryRecordingsFilters episodeHistoryRecordingsViews');
      var byDate = el('button', 'episodeHistoryRecordingsFilter', 'By date');
      byDate.type = 'button';
      byDate.setAttribute('data-eh-show', 'date');
      byDate.addEventListener('click', function () {
        saveView(currentView() === 'date' ? 'cards' : 'date');
        render(section, currentFilter());
      });
      views.appendChild(byDate);
      header.appendChild(views);
    }
    section.appendChild(header);

    var scroller = el('div', 'padded-top-focusscale padded-bottom-focusscale episodeHistoryRecordingsScroller');
    itemsContainerInto(scroller);
    section.appendChild(scroller);
    if (view.name === 'tab') {
      section.appendChild(el('div', 'ehDate'));
    }
    return section;
  }

  function fill(section, data, filter) {
    [].slice.call(section.querySelectorAll('[data-filter]')).forEach(function (b) {
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
        partial: c.PartialCount || 0, partialMinutes: c.PartialMinutes, recording: c.RecordingCount || 0,
        stallText: c.StallText, stallTitle: c.StallTitle
      });
    }).join('');
  }

  function sectionLimit(section) {
    return parseInt(section.getAttribute('data-eh-limit'), 10) || null;
  }

  function render(section, filter) {
    var dateList = section.querySelector('.ehDate');
    var byDate = !!dateList && currentView() === 'date';
    [].slice.call(section.querySelectorAll('[data-eh-show]')).forEach(function (b) {
      b.classList.toggle('episodeHistoryRecordingsFilter-active', byDate);
    });
    section.querySelector('.episodeHistoryRecordingsScroller').style.display = byDate ? 'none' : '';
    if (dateList) {
      dateList.style.display = byDate ? '' : 'none';
    }
    if (byDate) {
      renderDate(section, filter);
      return;
    }
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

  /*
   * Live TV > Recordings as a list by date (SageTV's and Channels DVR's recent recordings): one
   * line per recording, newest first, grouped by day, each with the logo and number of the
   * channel it came from, and the selected one's details beside the list. Data from
   * GET /EpisodeHistory/Recordings/ByDate; where the logo goes (start or end of the line) is a
   * plugin setting. The details' "..." is the same menu as the cards'.
   */
  function storedView() {
    try {
      return window.localStorage.getItem(VIEW_KEY);
    } catch (e) {
      return null;
    }
  }

  function saveView(v) {
    try {
      window.localStorage.setItem(VIEW_KEY, v);
    } catch (e) {
      // Private mode: the choice lasts until the page reloads.
    }
  }

  function currentView() {
    return storedView() || (settings && settings.TabView) || 'cards';
  }

  function fetchByDate(filter) {
    if (dateInflight[filter]) {
      return dateInflight[filter];
    }
    var client = window.ApiClient;
    dateInflight[filter] = client.ajax({
      type: 'GET',
      url: client.getUrl('EpisodeHistory/Recordings/ByDate', { filter: filter }),
      dataType: 'json'
    }).then(function (data) {
      delete dateInflight[filter];
      dateCache[filter] = { at: Date.now(), data: data };
      return data;
    }, function (err) {
      delete dateInflight[filter];
      dateCache[filter] = { at: Date.now(), data: (dateCache[filter] && dateCache[filter].data) || null };
      throw err;
    });
    return dateInflight[filter];
  }

  function renderDate(section, filter) {
    [].slice.call(section.querySelectorAll('.episodeHistoryRecordingsFilter[data-filter]')).forEach(function (b) {
      b.classList.toggle('episodeHistoryRecordingsFilter-active', b.getAttribute('data-filter') === filter);
    });
    var cached = dateCache[filter];
    if (cached && cached.data && section.ehDateShown !== cached) {
      fillDate(section, cached);
    }
    if (!cached || Date.now() - cached.at > MAX_AGE_MS) {
      fetchByDate(filter).then(function () {
        var fresh = dateCache[filter];
        if (section.isConnected && currentView() === 'date' && currentFilter() === filter && fresh.data) {
          fillDate(section, fresh);
        }
      }).catch(function () { log('loading the list by date failed'); });
    }
  }

  function dayStart(d) {
    return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  }

  function dayLabel(d) {
    var days = Math.round((dayStart(new Date()) - dayStart(d)) / 86400000);
    if (days <= 0) {
      return 'Today';
    }
    if (days === 1) {
      return 'Yesterday';
    }
    if (days < 7) {
      return 'Last ' + d.toLocaleDateString(undefined, { weekday: 'long' });
    }
    return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', year: d.getFullYear() === new Date().getFullYear() ? undefined : 'numeric' });
  }

  function timeText(d) {
    return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  }

  function logoHtml(ch, big) {
    var box = '<span class="ehLogo' + (big ? ' ehLogo-big' : '') + '"' + (ch ? ' title="' + esc(ch.Name) + '"' : '') + '>';
    if (!ch) {
      return box + '</span>';
    }
    if (ch.HasLogo) {
      var src = window.ApiClient.getImageUrl(ch.Id, { type: 'Primary', maxHeight: big ? 96 : 120, quality: 90 });
      return box + '<img src="' + esc(src) + '" alt="' + esc(ch.Name) + '" loading="lazy"></span>';
    }
    return box + '<span class="ehLogoText">' + esc(ch.Name) + '</span></span>';
  }

  function episodeNumbers(r) {
    return r.Season != null && r.Episode != null ? 'S' + r.Season + ' E' + r.Episode : '';
  }

  function chipsHtml(r) {
    var html = '';
    if (r.Recording) {
      html += '<span class="ehChip ehChip-rec">Recording</span>';
    }
    if (r.StatusShort) {
      html += '<span class="ehChip ehChip-warn" title="' + esc(r.StatusText || '') + '">' + esc(r.StatusShort) + '</span>';
    } else if (r.Partial) {
      html += '<span class="ehChip ehChip-warn" title="Too short to count as recorded">Partial' + (r.Minutes ? ' · ' + r.Minutes + ' min' : '') + '</span>';
    }
    if (r.Archived) {
      html += '<span class="ehChip">Archived</span>';
    }
    return html;
  }

  function dotHtml(r) {
    var kind = r.Played ? 'yes' : r.PlayedPercent > 0 ? 'part' : 'no';
    var label = r.Played ? 'Watched' : r.PlayedPercent > 0 ? r.PlayedPercent + '% watched' : 'Not watched';
    return '<span class="ehDot ehDot-' + kind + '" role="img" aria-label="' + label + '" title="' + label + '"></span>';
  }

  function rowHtml(r, logoRight, selected) {
    var d = new Date(r.RecordedUtc);
    var sub = r.Type === 'Episode' ? (r.EpisodeTitle || '') : (r.Year ? String(r.Year) : '');
    var meta = [episodeNumbers(r), r.Minutes ? r.Minutes + ' min' : ''].filter(Boolean).join(' · ');
    var ch = r.Channel;
    var logo = '<span class="ehLogoCol">' + logoHtml(ch, false) + '<span class="ehChNum">' + esc(ch ? ch.Number || '' : '') + '</span></span>';
    return '<button type="button" class="ehDateRow' + (selected ? ' ehDateRow-selected' : '') + '" data-id="' + esc(r.Id) + '"'
      + ' data-type="' + esc(r.Type) + '" data-played="' + !!r.Played + '">'
      + dotHtml(r)
      + (logoRight ? '' : logo)
      + '<span class="ehDateText"><span class="ehDateTitle cardText-first">' + esc(r.Title) + '</span>'
      + (chipsHtml(r) ? '<span class="ehChips">' + chipsHtml(r) + '</span>' : '')
      + '<span class="ehDateSub"><span class="cardText-secondary">' + esc(sub) + '</span>'
      + (meta ? ' <span class="ehMeta">' + esc(meta) + '</span>' : '')
      + (logoRight ? ' <span class="ehMeta">' + esc(timeText(d)) + '</span>' : '') + '</span></span>'
      + (logoRight ? logo : '<span class="ehTime">' + esc(timeText(d)) + '</span>')
      + '</button>';
  }

  function detailHtml(r) {
    var d = new Date(r.RecordedUtc);
    var ch = r.Channel;
    var img = window.ApiClient.getImageUrl(r.ImageId, { type: 'Primary', maxWidth: 640, quality: 90 });
    var numbers = r.Season != null && r.Episode != null ? 'Season ' + r.Season + ', Episode ' + r.Episode : (r.Type === 'Movie' && r.Year ? String(r.Year) : '');
    var tags = [];
    if (r.Minutes) {
      tags.push(r.Minutes + ' min');
    }
    tags.push(r.Played ? 'Watched' : r.PlayedPercent > 0 ? r.PlayedPercent + '% watched' : 'Not watched');
    if (r.Archived) {
      tags.push('Archived');
    }
    var status = '';
    if (r.Recording) {
      status = '<div class="ehStatus ehStatus-rec">Recording now</div>';
    } else if (r.StatusText || r.Partial) {
      status = '<div class="ehStatus"><div class="ehStatusHead">' + esc(r.StatusShort || 'Partial recording') + '</div>'
        + '<div>' + esc(r.StatusText || 'Too short to count as recorded. The "…" menu can keep it or record it again.') + '</div></div>';
    }
    return '<img class="ehDetailImg" src="' + esc(img) + '" alt="">'
      + '<h2 class="ehDetailTitle">' + esc(r.Title) + '</h2>'
      + (r.EpisodeTitle ? '<div class="ehDetailEp">' + esc(r.EpisodeTitle) + '</div>' : '')
      + (numbers ? '<div class="ehDetailMeta">' + esc(numbers) + '</div>' : '')
      + '<div class="ehDetailChannel">' + (ch ? logoHtml(ch, true) : '')
      + '<span>' + esc([ch ? [ch.Number, ch.Name].filter(Boolean).join(' ') : '', dayLabel(d) + ' ' + timeText(d)].filter(Boolean).join(' · ')) + '</span></div>'
      + '<div class="ehTags">' + tags.map(function (t) { return '<span class="ehChip">' + esc(t) + '</span>'; }).join('') + '</div>'
      + status
      + (r.Overview ? '<p class="ehOverview">' + esc(r.Overview) + '</p>' : '')
      + '<div class="ehActions">'
      + '<button type="button" class="ehBtn ehBtn-primary" data-eh-act="play"><span class="material-icons play_arrow" aria-hidden="true"></span>Play</button>'
      + '<button type="button" class="ehBtn" data-eh-act="open"><span class="material-icons info" aria-hidden="true"></span>Open</button>'
      + '<button type="button" class="ehBtn" data-eh-act="menu" aria-label="More"><span class="material-icons more_horiz" aria-hidden="true"></span>More</button>'
      + '</div>';
  }

  function fillDate(section, cached) {
    section.ehDateShown = cached;
    var data = cached.data || {};
    var list = section.querySelector('.ehDate');
    var recordings = data.Recordings || [];
    if (!recordings.length) {
      list.innerHTML = '';
      list.appendChild(el('div', 'episodeHistoryRecordingsEmpty', 'No recordings yet.'));
      return;
    }
    var logoRight = data.DateLogo === 'right';
    var selected = section.getAttribute('data-eh-selected');
    if (!recordings.some(function (r) { return r.Id === selected; })) {
      selected = recordings[0].Id;
    }
    var html = '<div class="ehDateList' + (logoRight ? ' ehDateList-logoRight' : '') + '">';
    var day = null;
    recordings.forEach(function (r) {
      var d = new Date(r.RecordedUtc);
      var label = r.Recording ? 'Recording now' : dayLabel(d);
      if (label !== day) {
        if (day !== null) {
          html += '</div></section>';
        }
        day = label;
        html += '<section class="ehDay"><h3 class="ehDayHead">' + esc(label)
          + (r.Recording || label.indexOf('Last ') !== 0 && label !== 'Today' && label !== 'Yesterday' ? ''
            : ' <span class="ehDayDate">' + esc(d.toLocaleDateString(undefined, { month: 'long', day: 'numeric' })) + '</span>')
          + '</h3><div class="ehDayRows">';
      }
      html += rowHtml(r, logoRight, r.Id === selected);
    });
    html += '</div></section></div><aside class="ehDateDetail" aria-label="Selected recording"></aside>';
    list.innerHTML = html;
    showDetail(section, recordings, selected);
  }

  function showDetail(section, recordings, id) {
    var r = recordings.filter(function (x) { return x.Id === id; })[0];
    var aside = section.querySelector('.ehDateDetail');
    if (!r || !aside) {
      return;
    }
    section.setAttribute('data-eh-selected', id);
    [].slice.call(section.querySelectorAll('.ehDateRow')).forEach(function (row) {
      row.classList.toggle('ehDateRow-selected', row.getAttribute('data-id') === id);
    });
    aside.innerHTML = detailHtml(r);
  }

  // Something changed (watched, deleted, kept…): reload what the lists show.
  function changed() {
    cache = {};
    dateCache = {};
    scheduleCheck();
  }

  document.addEventListener('click', function (e) {
    var t = e.target;
    if (!t || !t.closest) {
      return;
    }
    var section = t.closest('.' + SECTION_CLASS);
    var row = t.closest('.ehDateRow');
    if (row && section) {
      var data = (section.ehDateShown && section.ehDateShown.data) || {};
      showDetail(section, data.Recordings || [], row.getAttribute('data-id'));
      return;
    }
    var act = t.closest('[data-eh-act]');
    if (!act || !section) {
      return;
    }
    var id = section.getAttribute('data-eh-selected');
    var selectedRow = id && section.querySelector('.ehDateRow[data-id="' + id + '"]');
    if (!selectedRow) {
      return;
    }
    var action = act.getAttribute('data-eh-act');
    if (action === 'play') {
      playItem(id);
    } else if (action === 'open') {
      window.location.hash = '#/details?id=' + id;
    } else if (action === 'menu') {
      openMenu(selectedRow, act);
    }
  });

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
      '.episodeHistoryStalledChip { background: #d84315; color: #fff; }',
      '.episodeHistoryRecordingChip { position: absolute; right: .4em; top: .4em; display: flex; align-items: center; gap: .15em;',
      '  background: #c62828; color: #fff; font-weight: 600; border-radius: .3em; padding: .1em .35em; font-size: .8em; }',
      '.episodeHistoryRecordingChip .material-icons { font-size: 1em; }',
      '.' + SHOW_CLASS + ' .episodeHistoryRecordingsItems { padding-left: 0; }',
      '.ehDate { display: flex; flex-wrap: wrap; gap: 1.5em; padding: .5em 3.3% 2em; align-items: flex-start; }',
      '.ehDateList { flex: 999 1 560px; min-width: 0; }',
      '.ehDateDetail { flex: 1 1 340px; min-width: 0; position: sticky; top: 5em; display: flex; flex-direction: column; gap: .7em; }',
      '.ehDay { margin-bottom: 1.4em; }',
      '.ehDayHead { margin: 0 0 .5em .2em; font-size: 1.05em; font-weight: 600; }',
      '.ehDayDate { font-weight: 400; opacity: .6; margin-left: .4em; }',
      '.ehDayRows { background: rgba(255,255,255,.04); border-radius: .7em; overflow: hidden; }',
      '.ehDateRow { all: unset; box-sizing: border-box; width: 100%; display: flex; gap: .9em; align-items: center; padding: .7em 1em;',
      '  border-top: 1px solid rgba(255,255,255,.06); cursor: pointer; }',
      '.ehDayRows > .ehDateRow:first-child { border-top: 0; }',
      '.ehDateRow:hover { background: rgba(255,255,255,.05); }',
      '.ehDateRow:focus-visible { outline: 2px solid #00a4dc; outline-offset: -2px; }',
      '.ehDateRow-selected { background: rgba(0,164,220,.16); box-shadow: inset 3px 0 0 #00a4dc; }',
      '.ehDot { width: .7em; height: .7em; border-radius: 50%; flex-shrink: 0; box-sizing: border-box; }',
      '.ehDot-no { background: #00a4dc; }',
      '.ehDot-part { border: 2px solid #00a4dc; background: linear-gradient(90deg, transparent 50%, #00a4dc 50%); }',
      '.ehDot-yes { border: 2px solid rgba(255,255,255,.35); }',
      '.ehLogoCol { width: 6.2em; flex-shrink: 0; display: flex; flex-direction: column; align-items: center; gap: .15em; }',
      '.ehLogo { width: 6.2em; height: 3.4em; box-sizing: border-box; padding: .3em .45em; border-radius: .45em; background: rgba(255,255,255,.06);',
      '  display: flex; align-items: center; justify-content: center; overflow: hidden; }',
      '.ehLogo img { max-width: 100%; max-height: 100%; object-fit: contain; }',
      '.ehLogoText { font-size: .72em; font-weight: 700; text-align: center; line-height: 1.15; opacity: .9; }',
      '.ehLogo-big { width: 4.6em; height: 2.6em; flex-shrink: 0; }',
      '.ehChNum { font-size: .8em; font-weight: 600; opacity: .75; min-height: 1em; }',
      '.ehDateText { flex-grow: 1; min-width: 0; display: flex; flex-direction: column; gap: .2em; }',
      '.ehDateTitle { font-weight: 600; font-size: 1.02em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
      '.ehDateSub { font-size: .85em; opacity: .85; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
      '.ehMeta { opacity: .7; margin-left: .5em; }',
      '.ehTime { font-size: .85em; opacity: .7; flex-shrink: 0; }',
      '.ehChips, .ehTags { display: flex; flex-wrap: wrap; gap: .35em; }',
      '.ehChip { font-size: .72em; padding: .1em .5em; border-radius: .35em; background: rgba(255,255,255,.1); }',
      '.ehChip-rec { background: #c62828; color: #fff; font-weight: 700; }',
      '.ehChip-warn { background: #f59e0b; color: #1a1a1a; font-weight: 600; }',
      '.ehDetailImg { width: 100%; max-width: 30em; aspect-ratio: 16 / 9; object-fit: cover; border-radius: .5em; background: rgba(255,255,255,.05); }',
      '.ehDetailTitle { margin: .2em 0 0; font-size: 1.4em; font-weight: 600; }',
      '.ehDetailEp { font-size: 1.1em; }',
      '.ehDetailMeta { opacity: .7; font-size: .9em; }',
      '.ehDetailChannel { display: flex; align-items: center; gap: .6em; font-size: .9em; opacity: .9; }',
      '.ehStatus { background: rgba(245,158,11,.14); border: 1px solid rgba(245,158,11,.45); border-radius: .5em; padding: .7em .9em; font-size: .9em; line-height: 1.45; }',
      '.ehStatusHead { font-weight: 600; color: #fbbf24; margin-bottom: .2em; }',
      '.ehStatus-rec { background: rgba(198,40,40,.18); border-color: rgba(198,40,40,.6); font-weight: 600; }',
      '.ehOverview { margin: 0; line-height: 1.55; opacity: .9; }',
      '.ehActions { display: flex; flex-wrap: wrap; gap: .6em; }',
      '.ehBtn { display: inline-flex; align-items: center; gap: .35em; min-height: 2.6em; padding: 0 1.1em; border-radius: .4em; border: 0;',
      '  background: rgba(255,255,255,.1); color: inherit; font: inherit; cursor: pointer; }',
      '.ehBtn:hover { background: rgba(255,255,255,.18); }',
      '.ehBtn-primary { background: #00a4dc; color: #fff; }',
      '.ehBtn-primary:hover { background: #0bb4ee; }'
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
        partial: r.Partial ? 1 : 0, partialMinutes: r.Minutes, recording: r.Recording ? 1 : 0,
        stallText: r.StallText, stallTitle: r.StallTitle
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
      if (card.classList.contains('ehDateRow')) {
        changed();
      }
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
    stopRecordingEntries(id, function (label, onClick) {
      if (box.isConnected) {
        var stop = menuItem('stop_circle', label, function () { closeMenu(); onClick(); });
        // Right under the title: it is the entry a recording in progress is opened for.
        box.insertBefore(stop, title.nextSibling);
      }
    }, function () {
      var chip = card.querySelector('.episodeHistoryRecordingChip');
      chip && chip.parentNode.removeChild(chip);
    });
    if (single) {
      var stateAfter = box.lastChild;
      recordingStateEntries(id, function (icon, label, onClick) {
        if (box.isConnected) {
          var entry = menuItem(icon, label, function () { closeMenu(); onClick(); });
          box.insertBefore(entry, stateAfter.nextSibling);
          stateAfter = entry;
        }
      }, function (text) {
        if (box.isConnected) {
          var why = el('div', null, text);
          why.style.cssText = 'padding:.5em 1em;font-size:.85em;line-height:1.35;color:#ffab91;border-bottom:1px solid rgba(255,255,255,.1);';
          box.insertBefore(why, title.nextSibling);
        }
      }, function () {
        var chip = card.querySelector('.episodeHistoryPartialChip');
        chip && chip.parentNode.removeChild(chip);
      });
    }
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
          var listed = card.classList.contains('ehDateRow');
          card.parentNode && card.parentNode.removeChild(card);
          if (listed) {
            changed();
          }
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

  /*
   * A partial or stalled recording (the tuner went quiet while it was being made): "Keep it"
   * counts it as recorded / clears the warning, "Record again" schedules the programme's next
   * airing and leaves this file alone. Calls add(icon, label, onClick) per action the caller
   * may take and explain(text) with the story of a stall.
   */
  function recordingStateEntries(id, add, explain, onKept) {
    var client = window.ApiClient;
    client.ajax({ type: 'GET', url: client.getUrl('EpisodeHistory/Item/' + id + '/RecordingState'), dataType: 'json' }).then(function (r) {
      if (!r || !(r.Partial || r.Stalled || r.Pieces > 1)) {
        return;
      }
      if (r.Stalled && r.Text && explain) {
        explain(r.Text);
      }
      if (r.CanKeep && r.Pieces > 1) {
        add('merge', 'Join the ' + r.Pieces + ' pieces into one recording', function () {
          client.ajax({ type: 'POST', url: client.getUrl('EpisodeHistory/Item/' + id + '/JoinPieces'), dataType: 'json' }).then(function () {
            cache = {};
            dateCache = {};
            showCache = {};
            notify('Joining the ' + r.Pieces + ' pieces. The one recording takes their place in a minute or two.');
          }, function () {
            notify('The pieces couldn\'t be joined.');
          });
        });
      }
      if (r.CanKeep && (r.Partial || r.Stalled)) {
        add('task_alt', r.Partial ? 'Keep it: count as recorded' : 'Keep it as it is', function () {
          client.ajax({ type: 'POST', url: client.getUrl('EpisodeHistory/Item/' + id + '/KeepRecording'), dataType: 'json' }).then(function () {
            cache = {};
            dateCache = {};
            showCache = {};
            log('kept recording ' + id);
            if (onKept) {
              onKept();
            }
            scheduleCheck();
          }, function () {
            notify('The recording couldn\'t be marked as kept.');
          });
        });
      }
      if (r.CanRecordAgain && (r.Partial || r.Stalled)) {
        add('replay', 'Record again at the next airing', function () {
          client.ajax({ type: 'POST', url: client.getUrl('EpisodeHistory/Item/' + id + '/RecordAgain'), dataType: 'json' }).then(function (a) {
            if (!a || (!a.Scheduled && !a.AlreadyScheduled)) {
              notify((a && a.Message) || 'The guide has no later airing of it yet.');
              return;
            }
            var when = new Date(a.Start).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
            notify((a.AlreadyScheduled ? 'A recording is already set for ' : 'Recording it again on ') + when
              + (a.Channel ? ' (' + a.Channel + ')' : '') + '. This recording is kept until you delete it.');
          }, function () {
            notify('The next airing couldn\'t be scheduled.');
          });
        });
      }
    }).catch(function () {});
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
        dateCache = {};
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

  /*
   * "Stop recording": for an item being recorded right now (or a show with an episode being
   * recorded), in our own card menu and in jellyfin-web's "..." sheet. Offered when the caller
   * may manage Live TV. A window asks first; what was recorded so far is kept.
   */
  // Calls add(label, onClick) once per recording in progress behind the item.
  function stopRecordingEntries(id, add, onStopped) {
    var client = window.ApiClient;
    client.ajax({ type: 'GET', url: client.getUrl('EpisodeHistory/Item/' + id + '/ActiveRecordings'), dataType: 'json' }).then(function (r) {
      if (!r || !r.CanStop) {
        return;
      }
      (r.Recordings || []).forEach(function (rec) {
        add(r.Recordings.length > 1 ? 'Stop recording: ' + rec.Name : 'Stop recording', function () {
          setTimeout(function () { confirmStop(rec, onStopped); }, 150);
        });
      });
    }).catch(function () {});
  }

  function confirmStop(rec, onStopped) {
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
    var h = el('h2', null, 'Stop recording?');
    h.style.cssText = 'margin:0 0 .5em;font-size:1.3em;font-weight:500;';
    box.appendChild(h);
    var p = el('div', null, rec.Name + ' is being recorded now. Stopping ends the recording here; what was recorded so far is kept.');
    p.style.cssText = 'margin-bottom:1.1em;line-height:1.4;';
    box.appendChild(p);
    var row = el('div');
    row.style.cssText = 'display:flex;gap:.8em;justify-content:flex-end;';
    var cancel = el('button', null, 'Keep recording');
    cancel.type = 'button';
    cancel.style.cssText = 'padding:.55em 1.2em;border-radius:6px;border:0;background:rgba(255,255,255,.12);color:inherit;font:inherit;cursor:pointer;';
    cancel.addEventListener('click', close);
    var go = el('button', null, 'Stop recording');
    go.type = 'button';
    go.style.cssText = 'padding:.55em 1.2em;border-radius:6px;border:0;background:#c62828;color:#fff;font:inherit;cursor:pointer;';
    go.addEventListener('click', function () {
      go.disabled = cancel.disabled = true;
      go.textContent = 'Stopping\u2026';
      var client = window.ApiClient;
      client.ajax({ type: 'POST', url: client.getUrl('EpisodeHistory/Item/' + rec.Id + '/StopRecording'), dataType: 'json' }).then(function () {
        close();
        cache = {};
        dateCache = {};
        showCache = {};
        log('stopped recording ' + rec.Id);
        if (onStopped) {
          onStopped();
        }
        scheduleCheck();
      }, function () {
        close();
        notify('The recording couldn\'t be stopped (it may have just ended).');
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
    function sheetEntry(dataId, icon, label, onClick) {
      var b = document.createElement('button');
      b.setAttribute('is', 'emby-button');
      b.type = 'button';
      b.className = 'listItem listItem-button actionSheetMenuItem emby-button';
      b.setAttribute('data-id', dataId);
      b.innerHTML = '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ' + icon + '" aria-hidden="true"></span>'
        + '<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText"></div></div>';
      b.querySelector('.actionSheetItemText').textContent = label;
      // No stopPropagation: the click bubbling up is what closes the sheet.
      b.addEventListener('click', onClick);
      return b;
    }
    client.getItem(client.getCurrentUserId(), id).then(function (item) {
      if (!item) {
        return;
      }
      if (item.Type === 'Series' || item.Type === 'Episode' || item.Type === 'Movie' || item.Type === 'Video') {
        var stopCount = 0;
        stopRecordingEntries(id, function (label, onClick) {
          if (scroller.isConnected) {
            var entry = sheetEntry('episodehistory-stoprecording-' + (stopCount++), 'stop_circle', label, onClick);
            scroller.insertBefore(entry, scroller.firstChild);
          }
        });
      }
      if (item.Type !== 'Series') {
        var stateCount = 0;
        recordingStateEntries(id, function (icon, label, onClick) {
          if (scroller.isConnected) {
            scroller.appendChild(sheetEntry('episodehistory-recordingstate-' + (stateCount++), icon, label, onClick));
          }
        }, function (text) {
          if (scroller.isConnected) {
            var why = document.createElement('div');
            why.style.cssText = 'padding:.6em 1.2em;font-size:.9em;line-height:1.35;color:#ffab91;max-width:26em;';
            why.textContent = text;
            scroller.insertBefore(why, scroller.firstChild);
          }
        });
        return;
      }
      showDeleteEntry(id, item.Name, function (label, onClick) {
        if (!scroller.isConnected || scroller.querySelector('[data-id="episodehistory-deleteall"]')) {
          return;
        }
        scroller.appendChild(sheetEntry('episodehistory-deleteall', 'delete_sweep', label, onClick));
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
