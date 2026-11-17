/* Classic worker; self-contained same-origin assets, no third-party runtime. */
'use strict';
importScripts('/route-packages-core.js?v=20261117');
self.onmessage = async event => {
    try {
        if (event.data?.type !== 'pack') throw new Error('不支持的打包操作。');
        const plan = RoutePackages.planFiles(event.data.files);
        let lastProgress = 0;
        const blob = await RoutePackages.makeZip(plan, { onProgress(progress) {
            const now = Date.now();
            if (now - lastProgress > 100 || progress.processed === progress.total) {
                self.postMessage({ type: 'progress', ...progress }); lastProgress = now;
            }
        } });
        self.postMessage({ type: 'complete', blob, fileCount: plan.entries.length, routeCount: plan.routeCount });
    } catch (error) {
        self.postMessage({ type: 'error', message: error?.message || '本机打包失败。' });
    }
};
