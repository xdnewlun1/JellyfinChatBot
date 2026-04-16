(function () {
    'use strict';

    // SVG icons — squid/tentacle motif for Cthuwu
    var ICON_CHAT = '<svg viewBox="0 0 24 24"><path d="M12 2C7.58 2 4 5.58 4 10v3c0 .55.45 1 1 1s1-.45 1-1v-3c0-3.31 2.69-6 6-6s6 2.69 6 6v3c0 .55.45 1 1 1s1-.45 1-1v-3c0-4.42-3.58-8-8-8z"/><circle cx="9.5" cy="9.5" r="1.3"/><circle cx="14.5" cy="9.5" r="1.3"/><path d="M7 13c-.55 0-1 .45-1 1 0 2.5-1 4-2 5-.3.3-.3.8 0 1.1.3.3.8.3 1.1 0 1.3-1.3 2.5-3.2 2.5-6.1 0-.55-.45-1-1-1zm4 1c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1s1-.45 1-1v-6c0-.55-.45-1-1-1zm4 0c-.55 0-1 .45-1 1 0 2.9 1.2 4.8 2.5 6.1.3.3.8.3 1.1 0 .3-.3.3-.8 0-1.1-1-1-2-2.5-2-5 0-.55-.45-1-1-1z"/></svg>';
    var ICON_SEND = '<svg viewBox="0 0 24 24"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>';
    var ICON_CLOSE = '&times;';

    // State
    var messages = [];
    var isOpen = false;
    var isLoading = false;
    var panelEl = null;
    var badgeEl = null;
    var messagesEl = null;
    var inputEl = null;
    var sendBtn = null;

    function getAuthToken() {
        // Try ApiClient first
        if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
            return window.ApiClient.accessToken();
        }
        // Fallback: read from credentials in localStorage
        try {
            var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = creds.Servers || [];
            for (var i = 0; i < servers.length; i++) {
                if (servers[i].AccessToken) {
                    return servers[i].AccessToken;
                }
            }
        } catch (e) {
            // ignore
        }
        return null;
    }

    function getApiBaseUrl() {
        if (window.ApiClient && window.ApiClient.serverAddress) {
            return typeof window.ApiClient.serverAddress === 'function'
                ? window.ApiClient.serverAddress()
                : window.ApiClient.serverAddress;
        }
        return '';
    }

    function apiFetch(url, options) {
        var token = getAuthToken();
        options = options || {};
        options.headers = options.headers || {};

        if (token) {
            options.headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
        }

        return fetch(getApiBaseUrl() + url, options);
    }

    // --- Playback Detection ---
    function isMediaPlaying() {
        // Check for active video elements
        var videos = document.querySelectorAll('video');
        for (var i = 0; i < videos.length; i++) {
            if (!videos[i].paused) {
                return true;
            }
        }
        // Check for Jellyfin's video player overlay
        if (document.querySelector('.videoPlayerContainer:not(.hide)')) {
            return true;
        }
        // Check for fullscreen
        if (document.fullscreenElement) {
            return true;
        }
        // Check URL patterns for playback pages
        var hash = window.location.hash || '';
        if (hash.indexOf('/video') !== -1 || hash.indexOf('playing') !== -1) {
            return true;
        }
        return false;
    }

    function updatePlaybackVisibility() {
        if (!badgeEl) return;
        var playing = isMediaPlaying();
        if (playing) {
            badgeEl.classList.add('chatbot-hidden');
            if (panelEl) panelEl.classList.add('chatbot-hidden');
        } else {
            badgeEl.classList.remove('chatbot-hidden');
            if (isOpen && panelEl) {
                panelEl.classList.remove('chatbot-hidden');
            }
        }
    }

    // --- Session Storage ---
    function saveConversation() {
        try {
            sessionStorage.setItem('chatbot_messages', JSON.stringify(messages));
        } catch (e) {
            // ignore
        }
    }

    function loadConversation() {
        try {
            // LOW-6: drop saved history if the auth token changed (user switched / logged out).
            var token = getAuthToken() || '';
            var savedToken = sessionStorage.getItem('chatbot_token');
            if (savedToken && savedToken !== token) {
                sessionStorage.removeItem('chatbot_messages');
                messages = [];
            }
            sessionStorage.setItem('chatbot_token', token);

            var saved = sessionStorage.getItem('chatbot_messages');
            if (saved) {
                messages = JSON.parse(saved);
            }
        } catch (e) {
            messages = [];
        }
    }

    // --- Rendering ---
    function scrollToBottom() {
        if (messagesEl) {
            messagesEl.scrollTop = messagesEl.scrollHeight;
        }
    }

    function renderMessages() {
        if (!messagesEl) return;

        var html = '';

        if (messages.length === 0) {
            html = '<div class="chatbot-welcome">' +
                ICON_CHAT +
                '<p><strong>Cthuwu</strong></p>' +
                '<p>ia ia~ ask me about movies and shows and I shall stir the depths of your library for you</p>' +
                '</div>';
        }

        for (var i = 0; i < messages.length; i++) {
            var msg = messages[i];
            html += '<div class="chatbot-message ' + sanitizeRole(msg.role) + '">' +
                formatMessage(msg.content) + '</div>';

            // Render media cards after assistant messages
            if (msg.role === 'assistant' && msg.libraryResults && msg.libraryResults.length > 0) {
                html += renderLibraryCards(msg.libraryResults);
            }
            if (msg.role === 'assistant' && msg.seerrResults && msg.seerrResults.length > 0) {
                html += renderSeerrCards(msg.seerrResults);
            }
        }

        if (isLoading) {
            html += '<div class="chatbot-typing">' +
                '<div class="chatbot-typing-dot"></div>' +
                '<div class="chatbot-typing-dot"></div>' +
                '<div class="chatbot-typing-dot"></div>' +
                '</div>';
        }

        messagesEl.innerHTML = html;

        // Bind request buttons
        var requestBtns = messagesEl.querySelectorAll('.chatbot-request-btn');
        for (var j = 0; j < requestBtns.length; j++) {
            requestBtns[j].addEventListener('click', handleRequestClick);
        }

        // Bind library card clicks
        var libCards = messagesEl.querySelectorAll('.chatbot-media-card-clickable');
        for (var k = 0; k < libCards.length; k++) {
            libCards[k].addEventListener('click', function () {
                openDetailsPage(this.getAttribute('data-library-id'));
            });
        }

        scrollToBottom();
    }

    function createPosterImg(src) {
        // Build image elements via DOM to avoid inline event handlers
        var img = document.createElement('img');
        img.className = 'chatbot-media-poster';
        img.loading = 'lazy';
        img.alt = '';
        img.src = src;
        img.addEventListener('error', function () { this.style.display = 'none'; });
        // Return outer HTML for string concatenation; wrapped in a container
        var wrap = document.createElement('span');
        wrap.appendChild(img);
        return wrap.innerHTML;
    }

    function renderLibraryCards(results) {
        var html = '';
        for (var i = 0; i < results.length; i++) {
            var r = results[i];
            var imgSrc = r.imageUrl || '';
            html += '<div class="chatbot-media-card chatbot-media-card-clickable" data-library-id="' + escapeHtml(String(r.id || '')) + '" title="Open details">';
            if (imgSrc) {
                html += createPosterImg(getApiBaseUrl() + imgSrc);
            }
            html += '<div class="chatbot-media-info">';
            html += '<p class="chatbot-media-title">' + escapeHtml(r.name) + '</p>';
            html += '<p class="chatbot-media-meta">' + escapeHtml(r.type) +
                (r.year ? ' &middot; ' + escapeHtml(String(r.year)) : '') + '</p>';
            if (r.overview) {
                html += '<p class="chatbot-media-overview">' + escapeHtml(r.overview) + '</p>';
            }
            html += '</div></div>';
        }
        return html;
    }

    function getServerId() {
        try {
            if (window.ApiClient && typeof window.ApiClient.serverId === 'function') {
                return window.ApiClient.serverId();
            }
            if (window.ApiClient && window.ApiClient._serverInfo && window.ApiClient._serverInfo.Id) {
                return window.ApiClient._serverInfo.Id;
            }
        } catch (e) {}
        return '';
    }

    function openDetailsPage(itemId) {
        if (!itemId) return;
        var serverId = getServerId();
        var hash = '#/details?id=' + encodeURIComponent(itemId);
        if (serverId) hash += '&serverId=' + encodeURIComponent(serverId);
        window.location.hash = hash;
    }

    function renderSeerrCards(results) {
        var html = '';
        for (var i = 0; i < results.length; i++) {
            var r = results[i];
            var statusText = r.status === 5 ? 'Available' : r.status === 4 ? 'Processing' : r.status === 3 ? 'Requested' : '';
            var posterSrc = r.posterPath ? 'https://image.tmdb.org/t/p/w92' + r.posterPath : '';
            html += '<div class="chatbot-media-card">';
            if (posterSrc) {
                html += createPosterImg(posterSrc);
            }
            html += '<div class="chatbot-media-info">';
            html += '<p class="chatbot-media-title">' + escapeHtml(r.title) + '</p>';
            html += '<p class="chatbot-media-meta">' + escapeHtml(r.mediaType) +
                (r.year ? ' &middot; ' + escapeHtml(String(r.year)) : '') +
                (statusText ? ' &middot; ' + statusText : '') + '</p>';
            if (r.overview) {
                html += '<p class="chatbot-media-overview">' + escapeHtml(r.overview) + '</p>';
            }
            if (!statusText || r.status < 3) {
                html += '<button class="chatbot-request-btn" data-media-type="' + escapeHtml(r.mediaType) +
                    '" data-media-id="' + escapeHtml(String(r.id)) + '">Request</button>';
            }
            html += '</div></div>';
        }
        return html;
    }

    function formatMessage(text) {
        // Escape first, then apply safe markdown formatting.
        // Since escapeHtml runs first, all user/LLM content is entity-encoded
        // before any HTML tags are introduced. The regex captures only operate
        // on already-escaped text, so injected tags cannot survive.
        text = escapeHtml(text);
        // Bold: **text**
        text = text.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
        // Italic: *text* (but not inside bold which already consumed **)
        text = text.replace(/(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)/g, '<em>$1</em>');
        return text;
    }

    var VALID_ROLES = { user: true, assistant: true, error: true };

    function sanitizeRole(role) {
        return VALID_ROLES[role] ? role : 'assistant';
    }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;')
            .replace(/\//g, '&#x2F;');
    }

    // --- Chat Actions ---
    function sendMessage() {
        if (isLoading || !inputEl) return;

        var text = inputEl.value.trim();
        if (!text) return;

        messages.push({ role: 'user', content: text });
        inputEl.value = '';
        isLoading = true;
        renderMessages();

        // Build API messages (only role + content)
        var apiMessages = messages
            .filter(function (m) { return m.role === 'user' || m.role === 'assistant'; })
            .map(function (m) { return { role: m.role, content: m.content }; });

        apiFetch('/ChatBot/Chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ messages: apiMessages })
        })
        .then(function (res) {
            if (!res.ok) {
                throw new Error('HTTP ' + res.status);
            }
            return res.json();
        })
        .then(function (data) {
            var assistantMsg = {
                role: 'assistant',
                content: data.reply || 'No response received.'
            };

            if (data.libraryResults && data.libraryResults.length > 0) {
                assistantMsg.libraryResults = data.libraryResults;
            }
            if (data.seerrResults && data.seerrResults.length > 0) {
                assistantMsg.seerrResults = data.seerrResults;
            }

            messages.push(assistantMsg);
            isLoading = false;
            saveConversation();
            renderMessages();
        })
        .catch(function (err) {
            messages.push({
                role: 'error',
                content: 'Failed to get a response. Make sure the plugin is configured correctly.'
            });
            isLoading = false;
            renderMessages();
        });
    }

    function handleRequestClick(e) {
        var btn = e.currentTarget;
        var mediaType = btn.getAttribute('data-media-type');
        var mediaId = parseInt(btn.getAttribute('data-media-id'), 10);
        openRequestModal(mediaType, mediaId, btn);
    }

    function openRequestModal(mediaType, mediaId, sourceBtn) {
        var overlay = document.createElement('div');
        overlay.className = 'chatbot-modal-overlay';
        overlay.innerHTML =
            '<div class="chatbot-modal">' +
              '<div class="chatbot-modal-header">' +
                '<span>Request ' + escapeHtml(mediaType === 'tv' ? 'TV Show' : 'Movie') + '</span>' +
                '<button class="chatbot-modal-close" title="Close">&times;</button>' +
              '</div>' +
              '<div class="chatbot-modal-body"><p class="chatbot-modal-loading">Loading options...</p></div>' +
              '<div class="chatbot-modal-footer">' +
                '<button class="chatbot-modal-cancel">Cancel</button>' +
                '<button class="chatbot-modal-submit" disabled>Request</button>' +
              '</div>' +
            '</div>';
        document.body.appendChild(overlay);

        function close() {
            if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
        }
        overlay.querySelector('.chatbot-modal-close').addEventListener('click', close);
        overlay.querySelector('.chatbot-modal-cancel').addEventListener('click', close);
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) close();
        });

        var bodyEl = overlay.querySelector('.chatbot-modal-body');
        var submitBtn = overlay.querySelector('.chatbot-modal-submit');

        apiFetch('/ChatBot/Seerr/RequestOptions?mediaType=' + encodeURIComponent(mediaType) +
                 '&tmdbId=' + encodeURIComponent(mediaId))
            .then(function (res) {
                if (!res.ok) throw new Error('HTTP ' + res.status);
                return res.json();
            })
            .then(function (opts) {
                renderRequestForm(bodyEl, submitBtn, opts, mediaType, mediaId, sourceBtn, close);
            })
            .catch(function () {
                bodyEl.innerHTML = '<p class="chatbot-modal-error">Could not load request options from Jellyseerr.</p>';
            });
    }

    function renderRequestForm(bodyEl, submitBtn, opts, mediaType, mediaId, sourceBtn, close) {
        var servers = (opts && opts.servers) || [];
        var seasons = (opts && opts.seasons) || [];

        if (servers.length === 0) {
            bodyEl.innerHTML = '<p class="chatbot-modal-error">No Jellyseerr ' +
                (mediaType === 'tv' ? 'Sonarr' : 'Radarr') + ' servers configured.</p>';
            return;
        }

        var defaultServer = servers.filter(function (s) { return s.isDefault && !s.is4k; })[0] || servers[0];

        var html = '';
        html += '<label class="chatbot-modal-label">Server</label>';
        html += '<select class="chatbot-modal-server">';
        for (var i = 0; i < servers.length; i++) {
            var s = servers[i];
            html += '<option value="' + s.id + '"' + (s === defaultServer ? ' selected' : '') + '>' +
                escapeHtml(s.name) + (s.is4k ? ' (4K)' : '') + '</option>';
        }
        html += '</select>';

        html += '<label class="chatbot-modal-label">Quality Profile</label>';
        html += '<select class="chatbot-modal-profile"></select>';

        html += '<label class="chatbot-modal-label">Root Folder</label>';
        html += '<select class="chatbot-modal-root"></select>';

        if (mediaType === 'tv') {
            html += '<label class="chatbot-modal-label">Seasons</label>';
            html += '<div class="chatbot-modal-seasons">';
            html += '<label class="chatbot-season-row"><input type="checkbox" class="chatbot-season-all" checked> <span>All Seasons</span></label>';
            for (var j = 0; j < seasons.length; j++) {
                var sn = seasons[j];
                if (sn.seasonNumber === 0) continue;
                html += '<label class="chatbot-season-row"><input type="checkbox" class="chatbot-season" value="' +
                    sn.seasonNumber + '" checked> <span>' + escapeHtml(sn.name) +
                    (sn.episodeCount ? ' (' + sn.episodeCount + ' ep)' : '') + '</span></label>';
            }
            html += '</div>';
        }

        bodyEl.innerHTML = html;

        var serverSel = bodyEl.querySelector('.chatbot-modal-server');
        var profileSel = bodyEl.querySelector('.chatbot-modal-profile');
        var rootSel = bodyEl.querySelector('.chatbot-modal-root');

        function populateServerOptions() {
            var selId = parseInt(serverSel.value, 10);
            var srv = servers.filter(function (x) { return x.id === selId; })[0];
            if (!srv) return;

            profileSel.innerHTML = '';
            var profs = srv.profiles || [];
            for (var i = 0; i < profs.length; i++) {
                var p = profs[i];
                var sel = p.id === srv.activeProfileId ? ' selected' : '';
                profileSel.innerHTML += '<option value="' + p.id + '"' + sel + '>' + escapeHtml(p.name) + '</option>';
            }

            rootSel.innerHTML = '';
            var roots = srv.rootFolders || [];
            for (var k = 0; k < roots.length; k++) {
                var rp = roots[k].path;
                var selr = rp === srv.activeDirectory ? ' selected' : '';
                rootSel.innerHTML += '<option value="' + escapeHtml(rp) + '"' + selr + '>' + escapeHtml(rp) + '</option>';
            }
        }
        serverSel.addEventListener('change', populateServerOptions);
        populateServerOptions();

        if (mediaType === 'tv') {
            var allCb = bodyEl.querySelector('.chatbot-season-all');
            var seasonCbs = bodyEl.querySelectorAll('.chatbot-season');
            allCb.addEventListener('change', function () {
                for (var i = 0; i < seasonCbs.length; i++) seasonCbs[i].checked = allCb.checked;
            });
            for (var i = 0; i < seasonCbs.length; i++) {
                seasonCbs[i].addEventListener('change', function () {
                    var all = true;
                    for (var j = 0; j < seasonCbs.length; j++) if (!seasonCbs[j].checked) { all = false; break; }
                    allCb.checked = all;
                });
            }
        }

        submitBtn.disabled = false;
        submitBtn.addEventListener('click', function () {
            var selId = parseInt(serverSel.value, 10);
            var srv = servers.filter(function (x) { return x.id === selId; })[0];
            var payload = {
                mediaType: mediaType,
                mediaId: mediaId,
                serverId: selId,
                profileId: parseInt(profileSel.value, 10),
                rootFolder: rootSel.value,
                is4k: !!(srv && srv.is4k)
            };
            if (mediaType === 'tv') {
                var allCb = bodyEl.querySelector('.chatbot-season-all');
                if (allCb && allCb.checked) {
                    // leave seasons unset; backend will send "all"
                } else {
                    var chosen = [];
                    var cbs = bodyEl.querySelectorAll('.chatbot-season');
                    for (var i = 0; i < cbs.length; i++) {
                        if (cbs[i].checked) chosen.push(parseInt(cbs[i].value, 10));
                    }
                    payload.seasons = chosen;
                }
            }

            submitBtn.disabled = true;
            submitBtn.textContent = 'Requesting...';

            apiFetch('/ChatBot/Seerr/Request', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            })
            .then(function (res) {
                if (!res.ok) {
                    return res.text().then(function (t) { throw new Error(t || ('HTTP ' + res.status)); });
                }
                return res.json();
            })
            .then(function () {
                if (sourceBtn) {
                    sourceBtn.textContent = 'Requested!';
                    sourceBtn.disabled = true;
                    sourceBtn.style.background = '#4caf50';
                }
                close();
            })
            .catch(function (err) {
                submitBtn.disabled = false;
                submitBtn.textContent = 'Request';
                var errEl = bodyEl.querySelector('.chatbot-modal-error');
                if (!errEl) {
                    errEl = document.createElement('p');
                    errEl.className = 'chatbot-modal-error';
                    bodyEl.appendChild(errEl);
                }
                errEl.textContent = 'Request failed: ' + (err.message || 'unknown error');
            });
        });
    }

    function clearConversation() {
        messages = [];
        try {
            sessionStorage.removeItem('chatbot_messages');
        } catch (e) {
            // ignore
        }
        renderMessages();
    }

    // --- UI Construction ---
    function createWidget() {
        // Badge button
        badgeEl = document.createElement('button');
        badgeEl.className = 'chatbot-badge';
        badgeEl.innerHTML = ICON_CHAT;
        badgeEl.title = 'Chat with Cthuwu';
        badgeEl.addEventListener('click', togglePanel);

        // Panel
        panelEl = document.createElement('div');
        panelEl.className = 'chatbot-panel chatbot-hidden';

        // Header
        var header = document.createElement('div');
        header.className = 'chatbot-header';
        header.innerHTML = '<div class="chatbot-header-title">' + ICON_CHAT +
            ' <span>Cthuwu</span></div>' +
            '<div class="chatbot-header-actions">' +
            '<button class="chatbot-header-btn chatbot-new-btn" title="New conversation">New</button>' +
            '<button class="chatbot-header-btn chatbot-close-btn" title="Close">' + ICON_CLOSE + '</button>' +
            '</div>';

        header.querySelector('.chatbot-new-btn').addEventListener('click', clearConversation);
        header.querySelector('.chatbot-close-btn').addEventListener('click', togglePanel);

        // Messages
        messagesEl = document.createElement('div');
        messagesEl.className = 'chatbot-messages';

        // Input area
        var inputArea = document.createElement('div');
        inputArea.className = 'chatbot-input-area';

        inputEl = document.createElement('textarea');
        inputEl.className = 'chatbot-input';
        inputEl.placeholder = 'whisper to Cthuwu...';
        inputEl.rows = 1;
        inputEl.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMessage();
            }
        });
        // Auto-resize
        inputEl.addEventListener('input', function () {
            this.style.height = 'auto';
            this.style.height = Math.min(this.scrollHeight, 80) + 'px';
        });

        sendBtn = document.createElement('button');
        sendBtn.className = 'chatbot-send-btn';
        sendBtn.innerHTML = ICON_SEND;
        sendBtn.title = 'Send';
        sendBtn.addEventListener('click', sendMessage);

        inputArea.appendChild(inputEl);
        inputArea.appendChild(sendBtn);

        panelEl.appendChild(header);
        panelEl.appendChild(messagesEl);
        panelEl.appendChild(inputArea);

        document.body.appendChild(panelEl);
        document.body.appendChild(badgeEl);

        // Load saved conversation
        loadConversation();
        renderMessages();
    }

    function togglePanel() {
        isOpen = !isOpen;
        if (isOpen) {
            panelEl.classList.remove('chatbot-hidden');
            inputEl.focus();
            scrollToBottom();
        } else {
            panelEl.classList.add('chatbot-hidden');
        }
    }

    // --- Initialization ---
    function init() {
        // Don't initialize if no auth token (not logged in)
        // Wait a bit for Jellyfin to initialize
        if (!getAuthToken()) {
            return;
        }

        createWidget();

        // Playback detection: check periodically
        setInterval(updatePlaybackVisibility, 2000);

        // Also observe DOM for video player
        var observer = new MutationObserver(function () {
            updatePlaybackVisibility();
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    // Wait for page to be ready and Jellyfin to be initialized
    function waitForJellyfin() {
        // If ApiClient exists or we can find a token, go ahead
        if (getAuthToken()) {
            init();
            return;
        }

        // Retry every 2 seconds for up to 30 seconds
        var attempts = 0;
        var interval = setInterval(function () {
            attempts++;
            if (getAuthToken()) {
                clearInterval(interval);
                init();
            } else if (attempts > 15) {
                clearInterval(interval);
            }
        }, 2000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', waitForJellyfin);
    } else {
        waitForJellyfin();
    }
})();
