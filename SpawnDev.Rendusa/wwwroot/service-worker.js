// Rendusa Service Worker — VFS Proxy
// Intercepts /vfs/* requests and streams data from the page's VFS via MessageChannel.
// All other requests pass through unchanged.

const VFS_PATH_PREFIX = '/vfs/';

self.addEventListener('install', () => {
    // Activate immediately
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    // Take control of all clients immediately
    event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Only intercept /vfs/* requests
    if (!url.pathname.startsWith(VFS_PATH_PREFIX)) {
        return; // Let the browser handle it normally
    }

    event.respondWith(handleVfsRequest(event));
});

/**
 * Handle a /vfs/* request by proxying to the page's VFS via MessageChannel.
 */
async function handleVfsRequest(event) {
    try {
        // Get the requesting client (page)
        const client = await getClient(event);
        if (!client) {
            return new Response('No client available', { status: 503 });
        }

        const url = new URL(event.request.url);
        const vfsPath = '/' + url.pathname.substring(VFS_PATH_PREFIX.length);

        // Parse Range header if present
        const rangeHeader = event.request.headers.get('Range');
        let rangeStart = 0;
        let rangeEnd = -1; // -1 = to end of file
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

        // Create MessageChannel for this request
        const channel = new MessageChannel();

        // Send vfs-open to client
        client.postMessage({
            type: 'vfs-open',
            path: vfsPath,
            rangeStart: rangeStart,
            rangeEnd: rangeEnd
        }, [channel.port2]);

        // Wait for vfs-meta response
        const meta = await waitForMessage(channel.port1, 'vfs-meta', 10000);

        if (meta.error) {
            return new Response(meta.error, { status: meta.status || 404 });
        }

        const totalSize = meta.totalSize;
        const contentType = meta.contentType || 'application/octet-stream';

        // Calculate actual range
        let actualStart = rangeStart;
        let actualEnd = rangeEnd >= 0 ? Math.min(rangeEnd, totalSize - 1) : totalSize - 1;
        let contentLength = actualEnd - actualStart + 1;

        // Build the ReadableStream with pull-based backpressure
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

                    // Request next chunk — use large chunks to reduce round-trip latency
                    const desiredSize = Math.max(controller.desiredSize || 1048576, 262144); // min 256KB
                    port.postMessage({
                        type: 'vfs-pull',
                        desiredSize: desiredSize
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

        // Build response headers
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

/**
 * Get the client (page) that made this request.
 */
async function getClient(event) {
    // Try to get the specific client that made the request
    if (event.clientId) {
        const client = await self.clients.get(event.clientId);
        if (client) return client;
    }
    if (event.resultingClientId) {
        const client = await self.clients.get(event.resultingClientId);
        if (client) return client;
    }

    // Fallback: get any window client
    const clients = await self.clients.matchAll({ type: 'window' });
    return clients[0] || null;
}

/**
 * Wait for a specific message type on a MessagePort.
 */
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
