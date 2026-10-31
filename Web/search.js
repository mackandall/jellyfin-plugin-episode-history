/*
 * Episode History — a list view for TV programs on the search page.
 *
 * jellyfin-web shows TV programs found by a search (Programs, or Movies/Shows/Sports/Kids/News
 * in a Live TV search) as rows of posters you scroll sideways. This adds a "Posters | List"
 * switch where those rows start; List hides them and shows what's coming up as one list,
 * soonest first, grouped by day like Live TV > Schedule: time, channel logo and number, title,
 * episode, New/Live/Premiere, and whether it's set to record. A row opens the program, as its
 * poster does, and its ⋮ opens jellyfin-web's own menu for it, as the poster's does (so the other
 * plugins' entries — Other airings…, Recording Favourites… — are there too). The choice is
 * remembered in this browser.
 *
 * The list is its own search for the same words, not the posters' programs: jellyfin-web's
 * rows hold an arbitrary 100 of the matches, aired ones included (a search for "news" here
 * matched 1445 programs). /Items ignores MinEndDate/HasAired for programs and /LiveTv/Programs
 * ignores searchTerm, so the matches are fetched sorted by start time and the first upcoming
 * one is found by bisecting with Limit=1 requests (about 11 for 1445). Channel logos take one
 * more request, as programs don't carry the logo's tag. Loaded by guidemenu.js, so no extra
 * index.html patch.
 *
 * VERSION-SENSITIVE FILE — built against this server's jellyfin-web 12.x:
 *   - The search page is React, `#/search?query=…`, with `div.searchResults` holding one
 *     `div.verticalSection` per kind of result, each with cards carrying data-id/data-type.
 *   - Program cards have data-type "Program" (TvProgram/LiveTvProgram accepted too).
 *   - Our bar and list sit inside `div.searchResults`, before the first program row; React
 *     leaves nodes it didn't make alone.
 *   - The ⋮ menu: inside an `emby-itemscontainer`, a `.itemAction[data-action="menu"]` button
 *     opens the menu for the nearest element with data-id/data-type/data-serverid (as on
 *     cards); the plugins' menu additions find it the same way, but only on a `.card` or
 *     `.listItem`, hence the row's listItem class.
 */
(function () {
  'use strict';

  var BAR_CLASS = 'ehSearchBar';
  var LIST_CLASS = 'ehSearchList';
  var HIDDEN_CLASS = 'ehSearchHidden';
  var VIEW_KEY = 'episodeHistorySearchView';
  var PROGRAM_TYPES = ['Program', 'TvProgram', 'LiveTvProgram'];
  var PAGE = 200;
  // Programs that started up to this long ago may still be on; ended ones are dropped after.
  var LOOK_BACK_MS = 6 * 3600 * 1000;
  var pending = false;
  var state = null;      // { term, first, total, programs, loading, failed } for the list shown

  function log(msg) {
    if (window.console && console.debug) {
      console.debug('[EpisodeHistory search] ' + msg);
    }
  }

  function esc(text) {
    return String(text === undefined || text === null ? '' : text)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  function el(tag, className, text) {
    var e = document.createElement(tag);
    if (className) {
      e.className = className;
    }
    if (text != null) {
      e.textContent = text;
    }
    return e;
  }

  function currentView() {
    try {
      return localStorage.getItem(VIEW_KEY) === 'list' ? 'list' : 'posters';
    } catch (e) {
      return 'posters';
    }
  }

  function saveView(view) {
    try {
      localStorage.setItem(VIEW_KEY, view);
    } catch (e) { /* private mode: just not remembered */ }
  }

  function onSearchPage() {
    return (window.location.hash || '').indexOf('#/search') === 0;
  }

  function searchTerm() {
    var hash = window.location.hash || '';
    var q = hash.indexOf('?') >= 0 ? hash.slice(hash.indexOf('?') + 1) : '';
    var m = /(?:^|&)query=([^&]*)/.exec(q);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')).trim() : '';
  }

  // Result rows whose cards are all TV programs.
  function programSections(results) {
    return [].slice.call(results.children).filter(function (section) {
      var cards = section.querySelectorAll('.card[data-id]');
      return cards.length && [].every.call(cards, function (c) {
        return PROGRAM_TYPES.indexOf(c.getAttribute('data-type')) >= 0;
      });
    });
  }

  function addStyles() {
    if (document.getElementById('episodeHistorySearchStyles')) {
      return;
    }
    var style = el('style');
    style.id = 'episodeHistorySearchStyles';
    style.textContent = [
      '.' + HIDDEN_CLASS + ' { display: none !important; }',
      '.' + BAR_CLASS + ' { display: flex; align-items: center; gap: .6em; padding: 1em 3.3% 0; }',
      '.' + BAR_CLASS + ' .ehSearchLabel { opacity: .7; margin-right: .2em; }',
      '.' + BAR_CLASS + ' button, .' + LIST_CLASS + ' .ehSMore { padding: .35em .9em; border-radius: 1em; border: 0;',
      '  background: rgba(128,128,128,.2); color: inherit; font: inherit; font-size: .9em; cursor: pointer; }',
      '.' + BAR_CLASS + ' button.ehSearchActive { background: #00a4dc; color: #fff; }',
      '.' + LIST_CLASS + ' { padding: .8em 3.3% 2em; max-width: 60em; }',
      '.' + LIST_CLASS + ' .ehSCount { opacity: .65; font-size: .9em; margin: 0 0 1em .2em; }',
      '.' + LIST_CLASS + ' .ehSDay { margin-bottom: 1.4em; }',
      '.' + LIST_CLASS + ' .ehSDayHead { margin: 0 0 .5em .2em; font-size: 1.05em; font-weight: 600; }',
      '.' + LIST_CLASS + ' .ehSDayDate { font-weight: 400; opacity: .6; margin-left: .4em; }',
      '.' + LIST_CLASS + ' .ehSRows { background: rgba(255,255,255,.04); border-radius: .7em; overflow: hidden; }',
      // jellyfin-web lays an itemsContainer out as one row of cards; ours is a column of rows.
      '.' + LIST_CLASS + ' .ehSRows.itemsContainer { display: flex; flex-direction: column; flex-wrap: nowrap; }',
      '.' + LIST_CLASS + ' .ehSRows .ehSRow { width: 100%; }',
      '.' + LIST_CLASS + ' .ehSRow { all: unset; box-sizing: border-box; width: 100%; display: flex; gap: .9em; align-items: center;',
      '  padding: .6em 1em; border-top: 1px solid rgba(255,255,255,.06); cursor: pointer; }',
      '.' + LIST_CLASS + ' .ehSRows > .ehSRow:first-child { border-top: 0; }',
      '.' + LIST_CLASS + ' .ehSRow:hover { background: rgba(255,255,255,.05); }',
      '.' + LIST_CLASS + ' .ehSRow:focus-visible { outline: 2px solid #00a4dc; outline-offset: -2px; }',
      '.' + LIST_CLASS + ' .ehSTime { width: 7.5em; flex-shrink: 0; font-size: .9em; }',
      '.' + LIST_CLASS + ' .ehSEnd { display: block; opacity: .6; font-size: .9em; }',
      '.' + LIST_CLASS + ' .ehSLogoCol { width: 5.6em; flex-shrink: 0; display: flex; flex-direction: column; align-items: center; gap: .15em; }',
      '.' + LIST_CLASS + ' .ehSLogo { width: 5.6em; height: 3em; box-sizing: border-box; padding: .3em .45em; border-radius: .45em;',
      '  background: rgba(255,255,255,.06); display: flex; align-items: center; justify-content: center; overflow: hidden; }',
      '.' + LIST_CLASS + ' .ehSLogo img { max-width: 100%; max-height: 100%; object-fit: contain; }',
      '.' + LIST_CLASS + ' .ehSLogoText { font-size: .72em; font-weight: 700; text-align: center; line-height: 1.15; opacity: .9; }',
      '.' + LIST_CLASS + ' .ehSChNum { font-size: .8em; font-weight: 600; opacity: .75; min-height: 1em; }',
      '.' + LIST_CLASS + ' .ehSText { flex-grow: 1; min-width: 0; display: flex; flex-direction: column; gap: .2em; }',
      '.' + LIST_CLASS + ' .ehSTitle { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
      '.' + LIST_CLASS + ' .ehSSub { font-size: .85em; opacity: .8; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
      '.' + LIST_CLASS + ' .ehSMenu { flex-shrink: 0; margin: -.3em -.5em -.3em 0; opacity: .75; }',
      '.' + LIST_CLASS + ' .ehSMenu:hover, .' + LIST_CLASS + ' .ehSMenu:focus { opacity: 1; }',
      '.' + LIST_CLASS + ' .ehSChips { display: flex; flex-wrap: wrap; gap: .35em; justify-content: flex-end; flex-shrink: 0; }',
      '.' + LIST_CLASS + ' .ehSChip { font-size: .72em; padding: .1em .5em; border-radius: .35em; background: rgba(255,255,255,.1); }',
      '.' + LIST_CLASS + ' .ehSChip-new { background: #2e7d32; color: #fff; font-weight: 600; }',
      '.' + LIST_CLASS + ' .ehSChip-live { background: #6a1b9a; color: #fff; font-weight: 600; }',
      '.' + LIST_CLASS + ' .ehSChip-rec { background: #c62828; color: #fff; font-weight: 700; }',
      '.' + LIST_CLASS + ' .ehSEmpty { opacity: .7; padding: 1em .2em; }'
    ].join('\n');
    document.head.appendChild(style);
  }

  function query(term, startIndex, limit) {
    var client = window.ApiClient;
    var url = client.getUrl('Users/' + client.getCurrentUserId() + '/Items', {
      SearchTerm: term,
      IncludeItemTypes: 'TvProgram',
      Recursive: true,
      SortBy: 'StartDate,SortName',
      SortOrder: 'Ascending',
      StartIndex: startIndex,
      Limit: limit,
      Fields: 'ChannelInfo',
      EnableImageTypes: 'Primary',
      ImageTypeLimit: 1,
      EnableTotalRecordCount: true
    });
    return client.ajax({ type: 'GET', url: url, dataType: 'json' });
  }

  // The index of the first match that started after `since` (the matches sorted by start time).
  function firstFrom(term, since) {
    return query(term, 0, 1).then(function (r) {
      var lo = 0;
      var hi = (r && r.TotalRecordCount) || 0;
      var total = hi;
      function step() {
        if (lo >= hi) {
          return { first: lo, total: total };
        }
        var mid = Math.floor((lo + hi) / 2);
        return query(term, mid, 1).then(function (m) {
          var p = m && m.Items && m.Items[0];
          if (p && new Date(p.StartDate) < since) {
            lo = mid + 1;
          } else {
            hi = mid;
          }
          return step();
        });
      }
      return step();
    });
  }

  // Program results don't carry their channel's logo tag; look the channels up once.
  var logoTags = {};

  function fetchLogos(programs) {
    var client = window.ApiClient;
    var missing = programs.map(function (p) { return p.ChannelId; }).filter(function (id, i, all) {
      return id && !(id in logoTags) && all.indexOf(id) === i;
    });
    if (!missing.length) {
      return Promise.resolve();
    }
    var url = client.getUrl('Users/' + client.getCurrentUserId() + '/Items', {
      Ids: missing.join(','),
      EnableImageTypes: 'Primary',
      ImageTypeLimit: 1
    });
    return client.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (r) {
      missing.forEach(function (id) { logoTags[id] = null; });
      ((r && r.Items) || []).forEach(function (ch) {
        logoTags[ch.Id] = (ch.ImageTags && ch.ImageTags.Primary) || null;
      });
    }, function () { /* text names instead of logos */ });
  }

  function loadMore(s, list) {
    s.loading = true;
    var at = s.first + s.programs.length;
    return query(s.term, at, PAGE).then(function (r) {
      var items = (r && r.Items) || [];
      return fetchLogos(items).then(function () {
        s.programs = s.programs.concat(items);
        s.total = (r && r.TotalRecordCount) || s.total;
        s.loading = false;
        if (state === s) {
          list.innerHTML = listHtml(s);
        }
      });
    });
  }

  function start(term, list) {
    var s = { term: term, first: 0, total: 0, programs: [], loading: true, failed: false };
    state = s;
    list.innerHTML = '<div class="ehSEmpty">Loading…</div>';
    firstFrom(term, new Date(Date.now() - LOOK_BACK_MS)).then(function (f) {
      s.first = f.first;
      s.total = f.total;
      return loadMore(s, list);
    }).catch(function () {
      s.loading = false;
      s.failed = true;
      log('loading the programs failed');
      if (state === s) {
        list.innerHTML = '<div class="ehSEmpty">Couldn\'t load the list. Posters still work.</div>';
      }
    });
  }

  function dayLabel(d) {
    var start = new Date();
    start.setHours(0, 0, 0, 0);
    var day = new Date(d);
    day.setHours(0, 0, 0, 0);
    var days = Math.round((day - start) / 86400000);
    if (days <= 0) {
      return 'Today';
    }
    if (days === 1) {
      return 'Tomorrow';
    }
    return d.toLocaleDateString(undefined, { weekday: 'long' });
  }

  function timeText(d) {
    return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  }

  function logoHtml(p) {
    var box = '<span class="ehSLogo" title="' + esc(p.ChannelName || '') + '">';
    var tag = p.ChannelId && logoTags[p.ChannelId];
    if (tag) {
      var src = window.ApiClient.getImageUrl(p.ChannelId, { type: 'Primary', tag: tag, maxHeight: 100, quality: 90 });
      return box + '<img src="' + esc(src) + '" alt="' + esc(p.ChannelName || '') + '" loading="lazy"></span>';
    }
    return box + '<span class="ehSLogoText">' + esc(p.ChannelName || '') + '</span></span>';
  }

  function chipsHtml(p) {
    var chips = [];
    if (p.TimerId) {
      chips.push('<span class="ehSChip ehSChip-rec" title="Set to record">● Will record</span>');
    } else if (p.SeriesTimerId) {
      chips.push('<span class="ehSChip" title="The series is set to record, but not this airing">Series set</span>');
    }
    if (p.IsLive) {
      chips.push('<span class="ehSChip ehSChip-live">Live</span>');
    }
    if (p.IsPremiere) {
      chips.push('<span class="ehSChip ehSChip-new">Premiere</span>');
    } else if (p.IsNew) {
      chips.push('<span class="ehSChip ehSChip-new">New</span>');
    }
    [['IsMovie', 'Movie'], ['IsSports', 'Sports'], ['IsKids', 'Kids'], ['IsNews', 'News']].forEach(function (k) {
      if (p[k[0]]) {
        chips.push('<span class="ehSChip">' + k[1] + '</span>');
      }
    });
    return chips.join('');
  }

  function rowHtml(p, onNow) {
    var start = new Date(p.StartDate);
    var end = p.EndDate ? new Date(p.EndDate) : null;
    var numbers = p.ParentIndexNumber != null && p.IndexNumber != null ? 'S' + p.ParentIndexNumber + ' E' + p.IndexNumber : '';
    var sub = [p.EpisodeTitle, numbers, p.IsMovie && p.ProductionYear ? String(p.ProductionYear) : ''].filter(Boolean).join(' · ');
    var serverId = window.ApiClient.serverId ? window.ApiClient.serverId() : (p.ServerId || '');
    return '<div class="ehSRow listItem" role="button" tabindex="0" data-id="' + esc(p.Id) + '" data-type="' + esc(p.Type || 'Program') + '"'
      + ' data-serverid="' + esc(serverId) + '" data-channelid="' + esc(p.ChannelId || '') + '" data-isfolder="false"'
      + ' data-startdate="' + esc(p.StartDate || '') + '" data-enddate="' + esc(p.EndDate || '') + '">'
      + '<span class="ehSTime">' + esc(onNow ? 'On now' : timeText(start))
      + (end ? '<span class="ehSEnd">until ' + esc(timeText(end)) + '</span>' : '') + '</span>'
      + '<span class="ehSLogoCol">' + logoHtml(p) + '<span class="ehSChNum">' + esc(p.ChannelNumber || '') + '</span></span>'
      + '<span class="ehSText"><span class="ehSTitle">' + esc(p.Name) + '</span>'
      + (sub ? '<span class="ehSSub">' + esc(sub) + '</span>' : '') + '</span>'
      + '<span class="ehSChips">' + chipsHtml(p) + '</span>'
      + '<button type="button" is="paper-icon-button-light" class="itemAction ehSMenu" data-action="menu" title="More" aria-label="More">'
      + '<span class="material-icons more_vert" aria-hidden="true"></span></button>'
      + '</div>';
  }

  function listHtml(s) {
    var now = new Date();
    var programs = s.programs.filter(function (p) {
      return p.StartDate && (!p.EndDate || new Date(p.EndDate) > now);
    });
    if (!programs.length && s.first + s.programs.length >= s.total) {
      return '<div class="ehSEmpty">Nothing coming up.</div>';
    }
    var remaining = s.total - s.first - s.programs.length;
    // The posters count these too, so say why the list has fewer.
    var aired = s.first + s.programs.length - programs.length;
    var html = '<div class="ehSCount">' + (remaining > 0 ? 'The next ' + programs.length + ' airings, soonest first.' : programs.length + (programs.length === 1 ? ' airing' : ' airings') + ', soonest first.')
      + (aired > 0 ? ' ' + aired + (aired === 1 ? ' that already aired isn\'t' : ' that already aired aren\'t') + ' shown.' : '') + '</div>';
    var day = null;
    programs.forEach(function (p) {
      var d = new Date(p.StartDate);
      var onNow = d <= now;
      var label = onNow ? 'On now' : dayLabel(d);
      if (label !== day) {
        if (day !== null) {
          html += '</div></section>';
        }
        day = label;
        html += '<section class="ehSDay"><h3 class="ehSDayHead">' + esc(label)
          + (onNow ? '' : ' <span class="ehSDayDate">' + esc(d.toLocaleDateString(undefined, { month: 'long', day: 'numeric' })) + '</span>')
          + '</h3><div is="emby-itemscontainer" class="itemsContainer ehSRows">';
      }
      html += rowHtml(p, onNow);
    });
    if (day !== null) {
      html += '</div></section>';
    }
    if (remaining > 0) {
      html += '<button type="button" class="ehSMore">Show more (' + remaining + ' left)</button>';
    }
    return html;
  }

  function ensureBar(anchor, page) {
    var bar = page.querySelector('.' + BAR_CLASS);
    if (!bar) {
      bar = el('div', BAR_CLASS);
      bar.appendChild(el('span', 'ehSearchLabel', 'On TV:'));
      [['posters', 'Posters'], ['list', 'List']].forEach(function (v) {
        var b = el('button', null, v[1]);
        b.type = 'button';
        b.setAttribute('data-eh-view', v[0]);
        b.addEventListener('click', function () {
          saveView(v[0]);
          check();
        });
        bar.appendChild(b);
      });
    }
    var list = page.querySelector('.' + LIST_CLASS);
    var after = list && list.nextSibling === anchor ? list : anchor;
    if (bar.nextSibling !== after) {
      anchor.parentNode.insertBefore(bar, after);
    }
    [].slice.call(bar.querySelectorAll('[data-eh-view]')).forEach(function (b) {
      b.classList.toggle('ehSearchActive', b.getAttribute('data-eh-view') === currentView());
    });
    return bar;
  }

  function removeOurs(root) {
    [].slice.call(root.querySelectorAll('.' + BAR_CLASS + ', .' + LIST_CLASS)).forEach(function (n) {
      n.parentNode.removeChild(n);
    });
    [].slice.call(root.querySelectorAll('.' + HIDDEN_CLASS)).forEach(function (n) {
      n.classList.remove(HIDDEN_CLASS);
    });
    state = null;
  }

  function check() {
    pending = false;
    if (!onSearchPage() || !window.ApiClient) {
      if (state || document.querySelector('.' + BAR_CLASS)) {
        removeOurs(document);
      }
      return;
    }
    var page = document.querySelector('.libraryPage:not(.hide)') || document.body;
    var results = page.querySelector('.searchResults');
    var sections = results ? programSections(results) : [];
    var term = searchTerm();
    if (!sections.length || !term) {
      removeOurs(page);
      return;
    }
    addStyles();
    var anchor = sections[0];
    ensureBar(anchor, page);
    var list = page.querySelector('.' + LIST_CLASS);
    if (currentView() !== 'list') {
      sections.forEach(function (s) { s.classList.remove(HIDDEN_CLASS); });
      if (list) {
        list.parentNode.removeChild(list);
      }
      state = null;
      return;
    }
    sections.forEach(function (s) { s.classList.add(HIDDEN_CLASS); });
    if (!list) {
      list = el('div', LIST_CLASS);
      state = null;
    }
    if (list.nextSibling !== anchor) {
      anchor.parentNode.insertBefore(list, anchor);
    }
    if (!state || state.term !== term) {
      start(term, list);
    }
  }

  function scheduleCheck() {
    if (!pending) {
      pending = true;
      setTimeout(check, 150);
    }
  }

  document.addEventListener('click', function (e) {
    var t = e.target;
    if (!t || !t.closest) {
      return;
    }
    var more = t.closest('.' + LIST_CLASS + ' .ehSMore');
    if (more) {
      if (state && !state.loading) {
        more.disabled = true;
        more.textContent = 'Loading…';
        var s = state;
        loadMore(s, more.closest('.' + LIST_CLASS)).catch(function () {
          s.loading = false;
          more.disabled = false;
          more.textContent = 'Couldn\'t load more. Try again';
        });
      }
      return;
    }
    var row = t.closest('.' + LIST_CLASS + ' .ehSRow');
    if (row && !t.closest('.itemAction')) {
      var serverId = window.ApiClient.serverId && window.ApiClient.serverId();
      window.location.hash = '#/details?id=' + row.getAttribute('data-id') + (serverId ? '&serverId=' + serverId : '');
    }
  });

  document.addEventListener('keydown', function (e) {
    var row = e.target && e.target.classList && e.target.classList.contains('ehSRow') && e.target.closest('.' + LIST_CLASS) ? e.target : null;
    if (row && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault();
      row.click();
    }
  });

  new MutationObserver(function () {
    if (onSearchPage() || state || document.querySelector('.' + BAR_CLASS)) {
      scheduleCheck();
    }
  }).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', scheduleCheck);
  scheduleCheck();
})();
