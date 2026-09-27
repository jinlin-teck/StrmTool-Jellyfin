const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../Configuration/configPage.html'), 'utf8');
const script = html.match(/<script[^>]*>([\s\S]*?)<\/script>/)[1];
const flush = () => new Promise(setImmediate);
const defaults = { EnableAutoExtract: true, EnableMediaInfoCache: true, VerifyStrmContentHash: true, RefreshDelayMs: 1000, MaxConcurrentExtract: 5, MetadataRestoreTimeoutMinutes: 5 };
function setup() {
    const elements = Object.fromEntries([...html.matchAll(/id="([^"]+)"/g)].map(m => [m[1], {
        value: '', checked: false, hidden: false, disabled: false, textContent: '', listeners: {},
        classList: { add() {}, remove() {} },
        addEventListener(event, handler) { (this.listeners[event] ??= []).push(handler); }
    }]));
    const inputs = [...html.matchAll(/<input\b[^>]*id="([^"]+)"/g)].map(m => elements[m[1]]);
    elements.configPage.querySelector = selector => elements[selector.slice(1)];
    elements.configPage.querySelectorAll = () => inputs;
    const requests = [], updates = [], listeners = {};
    const context = {
        document: { readyState: 'complete', hidden: false, querySelector: selector => elements[selector.slice(1)], documentElement: { getAttribute: () => 'en' }, addEventListener: (event, handler) => { listeners[event] = handler; } },
        window: {}, navigator: { language: 'en' }, setTimeout() {}, clearTimeout() {},
        ApiClient: {
            getPluginConfiguration: () => new Promise((resolve, reject) => requests.push({ resolve, reject })),
            updatePluginConfiguration: (_, config) => new Promise((resolve, reject) => updates.push({ config, resolve, reject }))
        }
    };
    vm.createContext(context);
    vm.runInContext(script, context);
    const edit = (id, value) => { elements[id].value = value; for (const handler of elements[id].listeners.input ?? []) handler.call(elements[id]); };
    return { context, elements, requests, updates, listeners, edit };
}
test('load failure blocks writes and supports retry', async () => {
    const s = setup(); s.requests[0].reject(new Error('offline')); await flush();
    assert.equal(s.elements.btnSave.disabled, true);
    assert.equal(s.elements.configLoadError.hidden, false);
    s.context.saveConfig(); assert.equal(s.updates.length, 0);
    s.elements.btnRetryLoad.listeners.click[0](); s.requests[1].resolve(defaults); await flush();
    assert.equal(s.elements.btnSave.disabled, false);
    assert.equal(s.elements.configLoadError.hidden, true);
});
test('visibility changes preserve unsaved edits', async () => {
    const s = setup(); s.requests[0].resolve(defaults); await flush();
    s.edit('txtRefreshDelay', '3500'); s.listeners.visibilitychange();
    assert.equal(s.requests.length, 1); assert.equal(s.elements.txtRefreshDelay.value, '3500');
});
test('older responses and responses arriving after an edit cannot overwrite fields', async () => {
    const s = setup(); s.context.loadConfig(); s.requests[1].resolve({ ...defaults, RefreshDelayMs: 2000 }); await flush();
    s.requests[0].resolve(defaults); await flush(); assert.equal(s.elements.txtRefreshDelay.value, 2000);
    s.context.loadConfig(); s.edit('txtRefreshDelay', '3500'); s.requests[2].resolve(defaults); await flush();
    assert.equal(s.elements.txtRefreshDelay.value, '3500');
});
test('delay is clamped and edits during save remain dirty', async () => {
    const s = setup(); s.requests[0].resolve(defaults); await flush();
    s.edit('txtRefreshDelay', '60000'); s.context.saveConfig();
    assert.equal(s.updates[0].config.RefreshDelayMs, 20000); assert.equal(s.elements.txtRefreshDelay.value, 20000);
    s.context.saveConfig(); assert.equal(s.updates.length, 1);
    s.edit('txtRefreshDelay', '3500'); s.updates[0].resolve(); await flush();
    s.listeners.visibilitychange(); assert.equal(s.requests.length, 1); assert.equal(s.elements.btnSave.disabled, false);
});
test('failed save retains edits and permits retry', async () => {
    const s = setup(); s.requests[0].resolve(defaults); await flush();
    s.edit('txtRefreshDelay', '3500'); s.context.saveConfig(); s.updates[0].reject(new Error('offline')); await flush();
    assert.equal(s.elements.btnSave.disabled, false); s.listeners.visibilitychange(); assert.equal(s.requests.length, 1);
    s.context.saveConfig(); assert.equal(s.updates[1].config.RefreshDelayMs, 3500);
});
