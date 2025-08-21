<template>
  <div class="card">
    <h3>Control Panel</h3>
    <div class="row">
      <button @click="onHello" :disabled="helloLoading">hello()</button>
      <button @click="onSnapshot" :disabled="snapshotLoading">snapshot()</button>
    </div>
    <div class="row">
      <label>base_interval_ms: <input type="number" v-model.number="baseInterval" min="100" step="100" /></label>
      <label>persist: <input type="checkbox" v-model="persist" /></label>
      <button @click="onSetConfig" :disabled="setcfgLoading">setConfig()</button>
    </div>
    <div class="row">
      <label>start modules: <input v-model="startModules" placeholder="cpu,mem" /></label>
      <button @click="onStart" :disabled="startLoading">start()</button>
      <button @click="onStop" :disabled="stopLoading">stop()</button>
    </div>
    <div class="row">
      <label>burst modules: <input v-model="burstModules" placeholder="cpu" /></label>
      <label>interval_ms: <input type="number" v-model.number="burstInterval" min="100" step="100" /></label>
      <label>ttl_ms: <input type="number" v-model.number="burstTtl" min="1000" step="500" /></label>
      <button @click="onBurst" :disabled="burstLoading">burstSubscribe()</button>
    </div>
    <div class="row">
      <label><input type="checkbox" v-model="subEnable" /> subscribe_metrics.enable</label>
      <button @click="onSubscribe" :disabled="subLoading">subscribe_metrics()</button>
      <span v-if="!isTauri" style="color:#888; font-size:12px;">(Web Mock 环境下此按钮无效)</span>
    </div>
    <div class="row">
      <label><input type="checkbox" v-model="logMetrics" @change="toggleMetricsLog" /> 日志记录 metrics 推送</label>
    </div>
    <div class="log">
      <textarea :value="logs.join('\n')" readonly></textarea>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';
import { service as api } from '../api/service';
import { ensureEventBridge } from '../api/rpc.tauri';
import { isTauriHost } from '../api/rpc';

const logs = ref<string[]>([]);
const baseInterval = ref<number>(1000);
const persist = ref<boolean>(false);
const startModules = ref<string>('cpu,mem');
const burstModules = ref<string>('cpu');
const burstInterval = ref<number>(1000);
const burstTtl = ref<number>(5000);
const log = (m: any) => logs.value.push(`[${new Date().toLocaleTimeString()}] ${typeof m === 'string' ? m : JSON.stringify(m)}`);

// 统一 6s 超时封装 + 800ms 最长 loading 展示，避免长时间占用交互
const withTimeout = async <T>(p: Promise<T>, ms = 6000): Promise<T> => {
  let timer: any; const t = new Promise<never>((_, rej) => { timer = setTimeout(() => rej(new Error('control timeout')), ms); });
  try { return await Promise.race([p, t]) as T; } finally { if (timer) clearTimeout(timer); }
};

const helloLoading = ref(false);
function onHello() {
  helloLoading.value = true; const lt = setTimeout(() => { helloLoading.value = false; }, 800);
  withTimeout(api.hello())
    .then(r => log(r))
    .catch((e:any) => log(e?.message || e))
    .finally(() => { clearTimeout(lt); helloLoading.value = false; });
}

const snapshotLoading = ref(false);
function onSnapshot() {
  snapshotLoading.value = true; const lt = setTimeout(() => { snapshotLoading.value = false; }, 800);
  withTimeout(api.snapshot())
    .then(r => log(r))
    .catch((e:any) => log(e?.message || e))
    .finally(() => { clearTimeout(lt); snapshotLoading.value = false; });
}

const setcfgLoading = ref(false);
function onSetConfig() {
  setcfgLoading.value = true; const lt = setTimeout(() => { setcfgLoading.value = false; }, 800);
  withTimeout(Promise.resolve(api.setConfig?.({ base_interval_ms: baseInterval.value, persist: persist.value }) as any))
    .then(r => log(r ?? 'ok'))
    .catch((e:any) => log(e?.message || e))
    .finally(() => { clearTimeout(lt); setcfgLoading.value = false; });
}

const startLoading = ref(false);
function onStart() {
  startLoading.value = true; const lt = setTimeout(() => { startLoading.value = false; }, 800);
  const modules = startModules.value.split(',').map(s=>s.trim()).filter(Boolean);
  withTimeout(Promise.resolve(api.start?.({ modules }) as any))
    .then(r => log(r ?? 'ok'))
    .catch((e:any) => log(e?.message || e))
    .finally(() => { clearTimeout(lt); startLoading.value = false; });
}

const stopLoading = ref(false);
function onStop() {
  stopLoading.value = true; const lt = setTimeout(() => { stopLoading.value = false; }, 800);
  withTimeout(Promise.resolve(api.stop?.({}) as any))
    .then(r => log(r ?? 'ok'))
    .catch((e:any) => log(e?.message || e))
    .finally(() => { clearTimeout(lt); stopLoading.value = false; });
}

const burstLoading = ref(false);
function onBurst() {
  burstLoading.value = true; const lt = setTimeout(() => { burstLoading.value = false; }, 800);
  const modules = burstModules.value.split(',').map(s=>s.trim()).filter(Boolean);
  withTimeout(Promise.resolve(api.burstSubscribe?.({ modules, interval_ms: burstInterval.value, ttl_ms: burstTtl.value }) as any))
    .then(r => log(r ?? 'ok'))
    .catch((e:any) => log(e?.message || e))
    .finally(() => { clearTimeout(lt); burstLoading.value = false; });
}

const subEnable = ref<boolean>(false);
const subLoading = ref(false);
const isTauri = ref<boolean>(isTauriHost());
function onSubscribe() {
  subLoading.value = true; const lt = setTimeout(() => { subLoading.value = false; }, 800);
  // 记录用户点击触发及当前 enable 值
  try { log({ event: 'user_click_subscribe', enable: subEnable.value }); } catch {}
  withTimeout(Promise.resolve(api.subscribeMetrics?.({ enable: subEnable.value }) as any))
    .then(r => log(r ?? 'ok'))
    .catch((e:any) => log(e?.message || e))
    .finally(() => { clearTimeout(lt); subLoading.value = false; });
}

// Metrics 推送日志开关
const logMetrics = ref(false);
let unlisten: (() => void) | null = null;
let unlistenBridge: (() => void) | null = null;
let unlistenBridgeAck: (() => void) | null = null;
let unlistenBridgeRx: (() => void) | null = null;
let unlistenBridgeErr: (() => void) | null = null;
let unlistenBridgeDisc: (() => void) | null = null;
function toggleMetricsLog() {
  if (unlisten) { try { unlisten(); } catch {} unlisten = null; }
  if (logMetrics.value) {
    try {
      unlisten = api.onMetrics?.((p: any) => log({ event: 'metrics', payload: p }));
      log('metrics log enabled');
    } catch (e:any) { log(e?.message || e); }
  } else {
    log('metrics log disabled');
  }
}

onMounted(async () => {
  // 启动事件桥（Tauri 可用，Web 环境会静默忽略内部调用）
  try { await ensureEventBridge(); } catch {}
  // 监听桥接订阅调试事件
  try {
    const { listen } = await import('@tauri-apps/api/event');
    unlistenBridge = await listen('bridge_subscribe', (e:any) => log({ event: 'bridge_subscribe', payload: e?.payload }));
    unlistenBridgeAck = await listen('bridge_subscribe_ack', (e:any) => log({ event: 'bridge_subscribe_ack', payload: e?.payload }));
    unlistenBridgeRx = await listen('bridge_rx', (e:any) => log({ event: 'bridge_rx', payload: e?.payload }));
    unlistenBridgeErr = await listen('bridge_error', (e:any) => log({ event: 'bridge_error', payload: e?.payload }));
    unlistenBridgeDisc = await listen('bridge_disconnected', (e:any) => log({ event: 'bridge_disconnected', payload: e?.payload }));
  } catch {}
  // 首次记录宿主
  try { isTauri.value = isTauriHost(); } catch {}
  log({ host: isTauri.value ? 'tauri' : 'web-mock' });
  // 事件桥为异步建立，延时复查一次宿主标记，若发生变化再记录一次
  setTimeout(() => {
    const before = isTauri.value;
    try { isTauri.value = isTauriHost(); } catch {}
    if (isTauri.value !== before) {
      log({ host: isTauri.value ? 'tauri' : 'web-mock' });
    }
  }, 1200);
});
onUnmounted(() => {
  if (unlisten) { try { unlisten(); } catch {} unlisten = null; }
  if (unlistenBridge) { try { unlistenBridge(); } catch {} unlistenBridge = null; }
  if (unlistenBridgeAck) { try { unlistenBridgeAck(); } catch {} unlistenBridgeAck = null; }
  if (unlistenBridgeRx) { try { unlistenBridgeRx(); } catch {} unlistenBridgeRx = null; }
  if (unlistenBridgeErr) { try { unlistenBridgeErr(); } catch {} unlistenBridgeErr = null; }
  if (unlistenBridgeDisc) { try { unlistenBridgeDisc(); } catch {} unlistenBridgeDisc = null; }
});
</script>

<style scoped>
.card { padding: 12px; border: 1px solid #eee; border-radius: 8px; }
.row { display: flex; gap: 8px; margin: 6px 0; flex-wrap: wrap; }
button { padding: 6px 10px; }
.log textarea { width: 100%; min-height: 120px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
</style>
