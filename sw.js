// v1.8.0: PWA Service Worker for iTracker6 template
var CACHE_NAME = 'itracker6-cache-v1';

// Install event - cache essential assets
self.addEventListener('install', function(event) {
    console.log('[ServiceWorker] Install');
    self.skipWaiting();
});

// Activate event - clean up old caches
self.addEventListener('activate', function(event) {
    console.log('[ServiceWorker] Activate');
    event.waitUntil(
        caches.keys().then(function(cacheNames) {
            return Promise.all(
                cacheNames.filter(function(cacheName) {
                    return cacheName !== CACHE_NAME;
                }).map(function(cacheName) {
                    return caches.delete(cacheName);
                })
            );
        })
    );
    return self.clients.claim();
});

// Fetch event - network first, fall back to cache
self.addEventListener('fetch', function(event) {
    event.respondWith(
        fetch(event.request)
            .then(function(response) {
                // Cache successful responses
                if(response.status === 200) {
                    var responseClone = response.clone();
                    caches.open(CACHE_NAME).then(function(cache) {
                        cache.put(event.request, responseClone);
                    });
                }
                return response;
            })
            .catch(function() {
                return caches.match(event.request);
            })
    );
});
