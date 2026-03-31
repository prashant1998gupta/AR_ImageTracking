/**
 * AR Analytics Tracker v1.0
 * Lightweight analytics for WebAR experiences
 * 
 * Usage: Add to your AR build's index.html:
 * <script src="https://yourserver.com/ar-analytics/tracker/ar-analytics.js" 
 *         data-project="YOUR_API_KEY"></script>
 */
(function() {
    'use strict';

    // â”€â”€â”€ Configuration â”€â”€â”€
    const SCRIPT_TAG = document.currentScript || document.querySelector('script[data-project]');
    const API_KEY = SCRIPT_TAG ? SCRIPT_TAG.getAttribute('data-project') : null;
    const API_BASE = 'https://myaranalytics.gamer.gd';
    const HEARTBEAT_INTERVAL = 30000; // 30 seconds
    const SESSION_TIMEOUT = 30 * 60 * 1000; // 30 minutes

    if (!API_KEY) {
        console.warn('[AR Analytics] No data-project attribute found. Analytics disabled.');
        return;
    }

    // â”€â”€â”€ Session & Visitor IDs â”€â”€â”€
    const STORAGE_PREFIX = 'ara_';

    function generateId() {
        return 'xxxxxxxxxxxx4xxxyxxxxxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
            var r = Math.random() * 16 | 0;
            return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
        });
    }

    function getVisitorId() {
        let vid = localStorage.getItem(STORAGE_PREFIX + 'visitor_id');
        if (!vid) {
            vid = generateId();
            localStorage.setItem(STORAGE_PREFIX + 'visitor_id', vid);
        }
        return vid;
    }

    function getSessionId() {
        let sid = sessionStorage.getItem(STORAGE_PREFIX + 'session_id');
        let lastActive = parseInt(sessionStorage.getItem(STORAGE_PREFIX + 'last_active') || '0');
        
        // Create new session if expired or missing
        if (!sid || (Date.now() - lastActive > SESSION_TIMEOUT)) {
            sid = generateId();
            sessionStorage.setItem(STORAGE_PREFIX + 'session_id', sid);
            sessionStorage.setItem(STORAGE_PREFIX + 'session_start', Date.now().toString());
        }
        sessionStorage.setItem(STORAGE_PREFIX + 'last_active', Date.now().toString());
        return sid;
    }

    // â”€â”€â”€ Device Detection â”€â”€â”€
    function getDeviceType() {
        const ua = navigator.userAgent;
        if (/tablet|ipad|playbook|silk/i.test(ua)) return 'tablet';
        if (/mobile|iphone|ipod|android.*mobile|blackberry|opera mini|IEMobile/i.test(ua)) return 'mobile';
        return 'desktop';
    }

    function getOS() {
        const ua = navigator.userAgent;
        if (/iPad|iPhone|iPod/.test(ua)) return 'iOS';
        if (/Android/.test(ua)) return 'Android';
        if (/Windows NT/.test(ua)) return 'Windows';
        if (/Mac OS X/.test(ua)) return 'macOS';
        if (/Linux/.test(ua)) return 'Linux';
        if (/CrOS/.test(ua)) return 'ChromeOS';
        return 'Unknown';
    }

    function getBrowser() {
        const ua = navigator.userAgent;
        if (/EdgA?\//.test(ua)) return 'Edge';
        if (/OPR\/|Opera/.test(ua)) return 'Opera';
        if (/SamsungBrowser/.test(ua)) return 'Samsung';
        if (/UCBrowser/.test(ua)) return 'UC Browser';
        if (/Firefox/.test(ua)) return 'Firefox';
        if (/CriOS/.test(ua)) return 'Chrome (iOS)';
        if (/Chrome/.test(ua)) return 'Chrome';
        if (/Safari/.test(ua)) return 'Safari';
        return 'Unknown';
    }

    function getUTMParams() {
        const params = new URLSearchParams(window.location.search);
        return {
            utm_source: params.get('utm_source') || '',
            utm_medium: params.get('utm_medium') || '',
            utm_campaign: params.get('utm_campaign') || '',
        };
    }

    // â”€â”€â”€ API Communication (Image Pixel â€” GET-based) â”€â”€â”€
    // Uses the same technique as Google Analytics: loads a 1x1 transparent GIF
    // with data encoded in query parameters. No CORS, no POST, works everywhere.
    function sendToAPI(action, data) {
        data.api_key = API_KEY;
        data.action = action;
        try {
            var encoded = btoa(JSON.stringify(data));
            var url = API_BASE + '/api/controllers/pixel.php?d=' + encodeURIComponent(encoded);
            var img = new Image();
            img.src = url;
        } catch(e) { /* silently fail */ }
    }

    // â”€â”€â”€ Initialization â”€â”€â”€
    const visitorId = getVisitorId();
    const sessionId = getSessionId();
    const sessionStart = Date.now();
    let arActivated = false;
    let trackingTimers = {}; // target_id â†’ timestamp for scan duration

    // Start session (using short keys to keep URL length small)
    const utm = getUTMParams();
    sendToAPI('session', {
        sid: sessionId,
        vid: visitorId,
        dt: getDeviceType(),
        os: getOS(),
        br: getBrowser(),
        lang: navigator.language || 'en',
        sw: screen.width,
        sh: screen.height,
        ref: document.referrer || '',
        us: utm.utm_source,
        um: utm.utm_medium,
        uc: utm.utm_campaign,
        url: window.location.href,
    });

    // Log page view event
    sendToAPI('event', {
        sid: sessionId,
        et: 'page_view',
        ed: { url: window.location.href, title: document.title },
    });

    // â”€â”€â”€ Heartbeat (session duration tracking) â”€â”€â”€
    let heartbeatTimer = setInterval(function() {
        const duration = Math.round((Date.now() - sessionStart) / 1000);
        sendToAPI('heartbeat', {
            sid: sessionId,
            dur: duration,
        });
    }, HEARTBEAT_INTERVAL);

    // Send final heartbeat on page unload
    window.addEventListener('beforeunload', function() {
        clearInterval(heartbeatTimer);
        const duration = Math.round((Date.now() - sessionStart) / 1000);
        sendToAPI('heartbeat', { sid: sessionId, dur: duration });
    });

    // â”€â”€â”€ Public API for AR Events â”€â”€â”€
    window.arAnalytics = {
        /**
         * Track a custom event
         * @param {string} eventType - Event name (e.g. 'ar_image_found')
         * @param {object} eventData - Additional data (e.g. { target_id: 'flower' })
         */
        track: function(eventType, eventData) {
            sendToAPI('event', {
                sid: sessionId,
                et: eventType,
                ed: eventData || {},
            });
        },

        /**
         * Track AR session start (webcam activated)
         */
        arSessionStart: function() {
            this.track('ar_session_start');
        },

        /**
         * Track image target found
         * @param {string} targetId - The image target ID
         */
        arImageFound: function(targetId) {
            // Track first activation per session
            if (!arActivated) {
                arActivated = true;
                this.track('ar_activation', { target_id: targetId });
            }
            this.track('ar_image_found', { target_id: targetId });
            trackingTimers[targetId] = Date.now();
        },

        /**
         * Track image target lost
         * @param {string} targetId - The image target ID
         */
        arImageLost: function(targetId) {
            let duration = 0;
            if (trackingTimers[targetId]) {
                duration = Math.round((Date.now() - trackingTimers[targetId]) / 1000);
                delete trackingTimers[targetId];
            }
            this.track('ar_image_lost', { target_id: targetId, duration: duration });
            if (duration > 0) {
                this.track('ar_scan_duration', { target_id: targetId, duration: duration });
            }
        },

        /**
         * Track CTA click (URL open, phone call, etc.)
         * @param {string} ctaType - Type: 'url', 'phone', 'email', 'share'
         * @param {string} value - The URL/phone/email value
         */
        arCtaClick: function(ctaType, value) {
            this.track('ar_cta_click', { type: ctaType, value: value });
        },

        /**
         * Track screenshot taken
         */
        arScreenshot: function() {
            this.track('ar_screenshot');
        },

        /**
         * Track error
         * @param {string} errorMessage - Error description
         */
        arError: function(errorMessage) {
            this.track('ar_error', { message: errorMessage });
        },

        /**
         * Track camera flip
         */
        arCameraFlip: function() {
            this.track('ar_camera_flip');
        },
    };

    // â”€â”€â”€ Auto-Hook into Imagine WebAR (if present) â”€â”€â”€
    // Override key functions to automatically track events
    (function autoHook() {
        // Wait for AR to initialize
        let hookAttempts = 0;
        const maxAttempts = 50; // 5 seconds max

        const hookInterval = setInterval(function() {
            hookAttempts++;
            if (hookAttempts > maxAttempts) {
                clearInterval(hookInterval);
                return;
            }

            // Hook into StartWebcam success
            if (window.unityInstance && !window._araHookedWebcam) {
                window._araHookedWebcam = true;
                
                // Intercept SendMessage to catch tracking events from Unity
                const originalSendMessage = window.unityInstance.SendMessage.bind(window.unityInstance);
                window.unityInstance.SendMessage = function(obj, method, param) {
                    // Track webcam start success
                    if (obj === 'ARCamera' && method === 'OnStartWebcamSuccess') {
                        window.arAnalytics.arSessionStart();
                    }
                    // Track image found/lost from Unity
                    if (method === 'OnTrackingFound') {
                        // This is handled internally by iTracker, not via SendMessage
                    }
                    return originalSendMessage(obj, method, param);
                };
            }

            // Hook into ShowError
            if (window.ShowError && !window._araHookedError) {
                window._araHookedError = true;
                const origShowError = window.ShowError;
                window.ShowError = function(error) {
                    window.arAnalytics.arError(error);
                    return origShowError(error);
                };
            }

            // Hook into ShowScreenshot
            if (window.ShowScreenshot && !window._araHookedScreenshot) {
                window._araHookedScreenshot = true;
                const origScreenshot = window.ShowScreenshot;
                window.ShowScreenshot = function(dataUrl) {
                    window.arAnalytics.arScreenshot();
                    return origScreenshot(dataUrl);
                };
            }

            // Hook into ShowConfirmUrl
            if (window.ShowConfirmUrl && !window._araHookedUrl) {
                window._araHookedUrl = true;
                const origConfirmUrl = window.ShowConfirmUrl;
                window.ShowConfirmUrl = function(url) {
                    window.arAnalytics.arCtaClick('url', url);
                    return origConfirmUrl(url);
                };
            }

            // Hook into FlipCam
            if (window.FlipCam && !window._araHookedFlip) {
                window._araHookedFlip = true;
                const origFlip = window.FlipCam;
                window.FlipCam = function() {
                    window.arAnalytics.arCameraFlip();
                    return origFlip.apply(this, arguments);
                };
            }

            // All hooks installed
            if (window._araHookedWebcam && window._araHookedError) {
                clearInterval(hookInterval);
                console.log('[AR Analytics] Hooks installed');
            }
        }, 100);
    })();

    console.log('[AR Analytics] Tracker initialized (project: ' + API_KEY.substring(0, 8) + '...)');
})();
