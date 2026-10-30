'use strict';
(async () => {
  const build = document.getElementById('buildInfo');
  const list = document.getElementById('licenseFiles');
  try {
    const response = await fetch('api/legal-info', { cache: 'no-store' });
    if (!response.ok) throw new Error('版本信息暂不可用');
    const info = await response.json();
    build.textContent = `${info.component} · 版本 ${info.version} · 源码提交：${info.revision || '构建时未提供'}`;
    const source = document.getElementById('sourceLink');
    // Only the fixed project repository is accepted as a source link.
    if (typeof info.sourceUrl === 'string' && /^https:\/\/github\.com\/kaedelcb\/better-genshin-impact(?:\/tree\/[0-9a-f]{40})?$/i.test(info.sourceUrl)) {
      source.href = info.sourceUrl;
      source.textContent = info.revision ? '对应版本源码' : '源码仓库';
    }
    list.replaceChildren();
    for (const file of info.licenseFiles || []) {
      if (!/^legal\/licenses\/[\w.%-]+$/.test(file.url)) continue;
      const item = document.createElement('li');
      const link = document.createElement('a');
      link.href = file.url; link.textContent = file.name;
      item.append(link); list.append(item);
    }
    if (!list.childElementCount) list.textContent = '文件清单暂不可用，请查看随包的第三方声明。';
  } catch {
    build.textContent = '版本信息暂不可用；许可证全文与第三方声明仍可直接查看。';
    list.textContent = '文件清单暂不可用，请查看随包的第三方声明。';
  }
})();
