// Rendusa Service Worker (Published) — VFS Proxy + Offline Cache
// Intercepts /vfs/* requests and streams data from the page's VFS via MessageChannel.
// All other requests use cache-first for offline support.

const VFS_PATH_PREFIX = '/vfs/';

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);

    // VFS requests → proxy to page
    if (url.pathname.startsWith(VFS_PATH_PREFIX)) {
        event.respondWith(handleVfsRequest(event));
        return;
    }

    // All other requests → cache-first
    event.respondWith(onFetch(event));
});

// === Offline Cache ===

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall(event) {
    console.info('Service worker: Install');
    // Skip waiting to activate immediately
    self.skipWaiting();

    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate(event) {
    console.info('Service worker: Activate');
    // Claim all clients immediately
    await self.clients.claim();

    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
}

async function onFetch(event) {
    let cachedResponse = null;
    if (event.request.method === 'GET') {
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);

        const request = shouldServeIndexHtml ? 'index.html' : event.request;
        const cache = await caches.open(cacheName);
        cachedResponse = await cache.match(request);
    }

    return cachedResponse || fetch(event.request);
}

// === VFS Proxy (shared with dev service worker) ===

async function handleVfsRequest(event) {
    try {
        const client = await getClient(event);
        if (!client) {
            return new Response('No client available', { status: 503 });
        }

        const url = new URL(event.request.url);
        const vfsPath = '/' + url.pathname.substring(VFS_PATH_PREFIX.length);

        const rangeHeader = event.request.headers.get('Range');
        let rangeStart = 0;
        let rangeEnd = -1;
        let isRangeRequest = false;

        if (rangeHeader) {
            const match = rangeHeader.match(/bytes=(\d+)-(\d*)/);
            if (match) {
                isRangeRequest = true;
                rangeStart = parseInt(match[1], 10);
                if (match[2]) {
                    rangeEnd = parseInt(match[2], 10);
                }
            }
        }

        const channel = new MessageChannel();

        client.postMessage({
            type: 'vfs-open',
            path: vfsPath,
            rangeStart: rangeStart,
            rangeEnd: rangeEnd
        }, [channel.port2]);

        const meta = await waitForMessage(channel.port1, 'vfs-meta', 10000);

        if (meta.error) {
            return new Response(meta.error, { status: meta.status || 404 });
        }

        const totalSize = meta.totalSize;
        const contentType = meta.contentType || 'application/octet-stream';

        let actualStart = rangeStart;
        let actualEnd = rangeEnd >= 0 ? Math.min(rangeEnd, totalSize - 1) : totalSize - 1;
        let contentLength = actualEnd - actualStart + 1;

        const port = channel.port1;
        let cancelled = false;

        const stream = new ReadableStream({
            pull(controller) {
                if (cancelled) {
                    controller.close();
                    return;
                }

                return new Promise((resolve, reject) => {
                    const onMessage = (evt) => {
                        const msg = evt.data;
                        if (msg.type === 'vfs-data') {
                            port.removeEventListener('message', onMessage);
                            if (msg.chunk && msg.chunk.byteLength > 0) {
                                controller.enqueue(new Uint8Array(msg.chunk));
                            }
                            if (msg.done) {
                                controller.close();
                                port.close();
                            }
                            resolve();
                        } else if (msg.type === 'vfs-error') {
                            port.removeEventListener('message', onMessage);
                            controller.error(new Error(msg.error));
                            port.close();
                            reject(new Error(msg.error));
                        }
                    };

                    port.addEventListener('message', onMessage);

                    const desiredSize = controller.desiredSize || 65536;
                    port.postMessage({
                        type: 'vfs-pull',
                        desiredSize: Math.max(desiredSize, 16384)
                    });
                });
            },

            cancel() {
                cancelled = true;
                try {
                    port.postMessage({ type: 'vfs-cancel' });
                    port.close();
                } catch { /* port may already be closed */ }
            }
        });

        const headers = {
            'Content-Type': contentType,
            'Accept-Ranges': 'bytes',
            'Content-Length': contentLength.toString()
        };

        if (isRangeRequest && totalSize > 0) {
            headers['Content-Range'] = `bytes ${actualStart}-${actualEnd}/${totalSize}`;
            return new Response(stream, {
                status: 206,
                statusText: 'Partial Content',
                headers: headers
            });
        } else {
            return new Response(stream, {
                status: 200,
                statusText: 'OK',
                headers: headers
            });
        }

    } catch (err) {
        console.error('[SW] VFS proxy error:', err);
        return new Response('VFS proxy error: ' + err.message, { status: 500 });
    }
}

async function getClient(event) {
    if (event.clientId) {
        const client = await self.clients.get(event.clientId);
        if (client) return client;
    }
    if (event.resultingClientId) {
        const client = await self.clients.get(event.resultingClientId);
        if (client) return client;
    }
    const clients = await self.clients.matchAll({ type: 'window' });
    return clients[0] || null;
}

function waitForMessage(port, expectedType, timeoutMs) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            port.removeEventListener('message', onMessage);
            reject(new Error(`Timeout waiting for ${expectedType}`));
        }, timeoutMs);

        const onMessage = (evt) => {
            if (evt.data && evt.data.type === expectedType) {
                clearTimeout(timer);
                port.removeEventListener('message', onMessage);
                resolve(evt.data);
            }
        };

        port.addEventListener('message', onMessage);
        port.start();
    });
}
