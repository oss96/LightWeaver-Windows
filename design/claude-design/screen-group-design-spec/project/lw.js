(function () {
  const ph = (base) => `repeating-linear-gradient(135deg, rgba(255,255,255,0.04) 0 6px, rgba(255,255,255,0.015) 6px 12px), ${base}`;
  const mac = {
    mac: true, win: false, os: 'macOS 26', osShort: 'mac',
    font: '-apple-system,BlinkMacSystemFont,"SF Pro Text","Helvetica Neue",Helvetica,sans-serif',
    mono: 'ui-monospace,"SF Mono",Menlo,monospace',
    icon: 'Material Symbols Rounded', iv: "'FILL' 0,'wght' 400,'opsz' 20", ivf: "'FILL' 1,'wght' 400,'opsz' 20",
    winBg: '#1c1c1e', layer: 'transparent', layerTop: 'none',
    winBorder: '1px solid rgba(255,255,255,0.13)', winShadow: '0 30px 80px rgba(0,0,0,0.45), 0 0 0 0.5px rgba(0,0,0,0.7)', winR: 18,
    barH: 58,
    text: 'rgba(255,255,255,0.92)', text2: 'rgba(235,235,245,0.62)', text3: 'rgba(235,235,245,0.38)',
    sep: 'rgba(255,255,255,0.09)', surface: 'rgba(255,255,255,0.05)', surface2: 'rgba(255,255,255,0.09)',
    hover: 'rgba(255,255,255,0.08)', press: 'rgba(255,255,255,0.14)', stroke: 'rgba(255,255,255,0.10)',
    accent: '#A3233B', accentHover: '#B42C46', accentText: '#ffffff', accentFg: '#F07085', accentSoft: 'rgba(163,35,59,0.28)',
    btnBg: 'rgba(255,255,255,0.10)', btnHover: 'rgba(255,255,255,0.15)', btnBorder: '0.5px solid rgba(255,255,255,0.12)', btnR: 999, btnH: 28,
    fieldBg: 'rgba(255,255,255,0.06)', fieldBorder: '0.5px solid rgba(255,255,255,0.14)', fieldBottom: '0.5px solid rgba(255,255,255,0.14)', fieldR: 8, fieldH: 28,
    cardR: 12, boxBg: 'rgba(255,255,255,0.045)', boxBorder: '0.5px solid rgba(255,255,255,0.08)', boxR: 12,
    glass: 'rgba(58,58,64,0.40)', glassBorder: '0.5px solid rgba(255,255,255,0.17)',
    glassShadow: 'inset 0 1px 0 rgba(255,255,255,0.13), inset 0 -1px 0 rgba(0,0,0,0.22), 0 6px 20px rgba(0,0,0,0.32)',
    glassBlur: 'blur(24px) saturate(1.8)', groupR: 22, sel: 'rgba(255,255,255,0.15)',
    popBg: 'rgba(42,42,48,0.74)', popBorder: '0.5px solid rgba(255,255,255,0.17)',
    popShadow: '0 18px 50px rgba(0,0,0,0.5), inset 0 1px 0 rgba(255,255,255,0.09)', popBlur: 'blur(30px) saturate(1.6)', popR: 16,
    miR: 7, miH: 26, menuHi: '#A3233B', menuHiText: '#ffffff', menuPad: 5,
    ovBg: 'rgba(16,16,18,0.55)', ovBorder: '0.5px solid rgba(255,255,255,0.10)', ovBlur: 'blur(20px) saturate(1.2)',
    ph: ph('#242428'), track: 'rgba(255,255,255,0.16)', toggleOff: 'rgba(255,255,255,0.16)',
    focus: '0 0 0 3.5px rgba(163,35,59,0.6)',
    backIcon: 'chevron_left', fwdIcon: 'chevron_right', segBg: 'rgba(255,255,255,0.07)', segSel: 'rgba(255,255,255,0.20)', segR: 999,
    menuIcon: 'keyboard_arrow_down', titleFont: '600 22px/1.2', h2: '600 15px/1.3',
    ctrlName: (m, w) => m
  };
  const win = {
    mac: false, win: true, os: 'Windows 11', osShort: 'win',
    font: '"Segoe UI Variable Text","Segoe UI","Open Sans",system-ui,sans-serif',
    mono: '"Cascadia Mono",Consolas,ui-monospace,monospace',
    icon: 'Material Symbols Outlined', iv: "'FILL' 0,'wght' 300,'opsz' 20", ivf: "'FILL' 1,'wght' 300,'opsz' 20",
    winBg: '#202020', layer: 'rgba(58,58,58,0.30)', layerTop: '1px solid rgba(255,255,255,0.06)',
    winBorder: '1px solid rgba(255,255,255,0.10)', winShadow: '0 32px 64px rgba(0,0,0,0.37), 0 2px 21px rgba(0,0,0,0.37)', winR: 8,
    barH: 56,
    text: '#ffffff', text2: 'rgba(255,255,255,0.786)', text3: 'rgba(255,255,255,0.544)',
    sep: 'rgba(255,255,255,0.0837)', surface: 'rgba(255,255,255,0.0512)', surface2: 'rgba(255,255,255,0.0837)',
    hover: 'rgba(255,255,255,0.0605)', press: 'rgba(255,255,255,0.0419)', stroke: 'rgba(255,255,255,0.0698)',
    accent: '#B83A52', accentHover: '#C44A61', accentText: '#ffffff', accentFg: '#F08C9C', accentSoft: 'rgba(184,58,82,0.26)',
    btnBg: 'rgba(255,255,255,0.0605)', btnHover: 'rgba(255,255,255,0.0837)', btnBorder: '1px solid rgba(255,255,255,0.0698)', btnR: 4, btnH: 32,
    fieldBg: 'rgba(255,255,255,0.0605)', fieldBorder: '1px solid rgba(255,255,255,0.0698)', fieldBottom: '1px solid rgba(255,255,255,0.5442)', fieldR: 4, fieldH: 32,
    cardR: 8, boxBg: 'rgba(255,255,255,0.0512)', boxBorder: '1px solid rgba(0,0,0,0.1)', boxR: 8,
    glass: 'rgba(255,255,255,0.0326)', glassBorder: '1px solid rgba(255,255,255,0.0698)', glassShadow: 'none', glassBlur: 'none', groupR: 8,
    sel: 'rgba(255,255,255,0.0605)',
    popBg: 'rgba(44,44,44,0.97)', popBorder: '1px solid rgba(255,255,255,0.09)', popShadow: '0 8px 16px rgba(0,0,0,0.26)', popBlur: 'blur(30px)', popR: 8,
    miR: 4, miH: 30, menuHi: 'rgba(255,255,255,0.0605)', menuHiText: '#ffffff', menuPad: 4,
    ovBg: 'rgba(28,28,28,0.66)', ovBorder: '1px solid rgba(255,255,255,0.08)', ovBlur: 'blur(20px)',
    ph: ph('#2a2a2a'), track: 'rgba(255,255,255,0.5442)', toggleOff: 'transparent',
    focus: '0 0 0 1px #000, 0 0 0 3px #fff',
    backIcon: 'arrow_back', fwdIcon: 'arrow_forward', segBg: 'rgba(255,255,255,0.0605)', segSel: 'rgba(255,255,255,0.0837)', segR: 4,
    menuIcon: 'expand_more', titleFont: '600 28px/1.2', h2: '600 15px/1.3'
  };
  const libs3 = [
    { icon: 'movie', label: 'Movies' }, { icon: 'tv', label: 'TV Shows' }, { icon: 'videocam', label: 'Home Videos' }
  ];
  const libs9 = libs3.concat([
    { icon: 'music_note', label: 'Music' }, { icon: 'stadium', label: 'Concerts' }, { icon: 'public', label: 'Documentaries' },
    { icon: 'child_care', label: 'Kids' }, { icon: 'headphones', label: 'Audiobooks' }, { icon: 'photo_library', label: 'Photos' }
  ]);
  const playlists = [
    { icon: 'grid_view', label: 'All' }, { icon: 'queue_music', label: 'Road Trip' }, { icon: 'playlist_play', label: 'Friday Night' },
    { icon: 'playlist_play', label: 'Kids Rotation' }, { icon: 'playlist_play', label: 'Rewatch 2026' }
  ];
  const movies = ['The Long Quiet', 'Harbor Lights', 'Northbound', 'Paper Moons', 'Iron Orchard', 'Glass Season', 'Salt & Cedar', 'The Ninth Hour', 'Low Tide', 'Kestrel', 'Midnight Ferry', 'Copper Coast', 'A Field of Bells', 'Static Bloom', 'Wren', 'The Cartographer', 'Ember Road', 'Parallel Hearts', 'Quiet Engines', 'Westerly', 'Hollow Pines', 'Blue Hour', 'Second Light', 'Marrow', 'Lanterns at Sea', 'Orbit of Rain', 'Halcyon', 'Juniper Street', 'The Last Signal', 'Undertow', 'Fieldnotes', 'Monsoon Club'];
  const years = [2019, 2004, 2022, 1998, 2015, 2021, 2011, 2008, 2024, 2017, 2001, 2013, 2023, 1995, 2020, 2006];
  const shows = ['Lantern House', 'The Dispatch', 'Borderland', 'Night Shift', 'Tidewater', 'Common Ground', 'Signal Fire', 'The Annex', 'Greyfield', 'Moth & Flame'];
  const people = [['Ada Moreno', 'Mara Quill'], ['Tomas Adler', 'Director'], ['Lin Okafor', 'Jonah'], ['Rhea Castell', 'Ines'], ['Sami Varga', 'Writer'], ['Noor Haddad', 'Captain Ross'], ['Felix Brandt', 'Composer'], ['Ines Albright', 'Older Mara'], ['Kenji Moll', 'Cinematography'], ['Petra Lund', 'Editor']];
  const az = ['All', '#'].concat('ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split(''));
  const azCompact = ['All', '#'].concat('ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').map((l, i) => (i % 2 ? '•' : l)));
  function pair(flags, w, h) {
    return [Object.assign({}, mac, { w: w || 1440, h: h || 900 }, flags || {}), Object.assign({}, win, { w: w || 1440, h: h || 900 }, flags || {})];
  }
  function movieCards(n, start) {
    const out = [];
    for (let i = 0; i < n; i++) {
      const k = (i + (start || 0));
      out.push({ title: movies[k % movies.length], sub: String(years[k % years.length]), label: 'poster', key: k });
    }
    return out;
  }
  function home(hover) {
    const cw = [0.62, 0.18, 0.85, 0.4, 0.07, 0.5].map((pr, i) => ({ title: i % 2 ? shows[i] : movies[i + 3], sub: i % 2 ? 'S2 · E' + (i + 3) + ' · 24 min left' : '1 h 12 min left', progress: pr, label: 'backdrop', hover: hover && i === 0 }));
    const nu = [0, 1, 2, 3, 4, 5].map(i => ({ title: shows[(i + 2) % shows.length], sub: 'S' + (i % 3 + 1) + ' · E' + (i + 4) + ' — Chapter ' + (i + 4), label: 'episode still' }));
    const ra = movieCards(12, 8);
    return { cw, nu, ra };
  }
  window.LW = { home, mac, win, libs3, libs9, playlists, movies, years, shows, people, az, azCompact, pair, movieCards, pin: '#FF8A1F' };
})();
