<template>
  <div class="card">
    <h3>Snapshot</h3>
    <div class="row">
      <button @click="refresh" :disabled="loading">刷新</button>
      <div class="checks">
        <label><input type="checkbox" v-model="m.cpu" /> cpu</label>
        <label><input type="checkbox" v-model="m.memory" /> memory</label>
        <label><input type="checkbox" v-model="m.disk" /> disk</label>
        <label><input type="checkbox" v-model="m.network" /> network</label>
        <label><input type="checkbox" v-model="m.gpu" /> gpu</label>
        <label><input type="checkbox" v-model="m.sensor" /> sensor</label>
      </div>
      <button @click="refreshWithModules" :disabled="loading">带参刷新</button>
      <span v-if="loading">加载中…</span>
      <span v-else>CPU: {{ snap?.cpu?.usage_percent?.toFixed(1) ?? '-' }}% | 内存: {{ snap?.memory?.used ?? '-' }}/{{ snap?.memory?.total ?? '-' }} MB</span>
      <span v-if="error" style="color:#c00; font-size:12px; margin-left:8px;">{{ error }}</span>
    </div>
    <div class="row small">
      <span class="dot" :class="{ on: connected }"></span>
      <span>连接状态：{{ connected ? '已连接' : '未连接' }}</span>
      <span v-if="lastEvent">（{{ lastEvent }}）</span>
      <span style="flex:1"></span>
      <label class="toggle"><input type="checkbox" v-model="showRaw" /> 显示原始 JSON</label>
    </div>
    <pre v-if="showRaw" class="raw">{{ rawJson }}</pre>
    <div v-else class="small kv-list">
      <!-- 仅展示返回的字段：动态渲染（排除 ts） -->
      <div v-if="snap">
        <div v-for="(val, key) in filteredEntries" :key="key">
          <strong>{{ key }}</strong>: {{ pretty(val) }}
        </div>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue';
import type { SnapshotResult, SnapshotParams } from '../api/dto';
import { service } from '../api/service';
import { ensureEventBridge } from '../api/rpc.tauri';

const snap = ref<SnapshotResult | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
let reqSeq = 0;
const connected = ref(false);
const lastEvent = ref('');
const showRaw = ref(false);
const rawJson = computed(() => snap.value ? JSON.stringify(snap.value, null, 2) : '');
const m = ref({ cpu: true, memory: true, disk: false, network: false, gpu: false, sensor: false });
const filteredEntries = computed(() => {
  const s: any = snap.value || {};
  const out: Record<string, any> = {};
  for (const k of Object.keys(s)) { if (k !== 'ts') out[k] = (s as any)[k]; }
  return out;
});
const pretty = (v: any) => {
  if (v == null) return '';
  if (typeof v === 'object') {
    try {
      const s = JSON.stringify(v);
      return s.length > 300 ? s.slice(0, 300) + '…' : s;
    } catch {
      return '[object]';
    }
  }
  return String(v);
};
const withTimeout = async <T>(p: Promise<T>, ms = 6000): Promise<T> => {
  let timer: any; const t = new Promise<never>((_, rej) => { timer = setTimeout(() => rej(new Error('snapshot timeout')), ms); });
  try { return await Promise.race([p, t]) as T; } finally { if (timer) clearTimeout(timer); }
};
const refresh = () => {
  const my = ++reqSeq; error.value = null;
  loading.value = true; const lt = setTimeout(() => { if (my === reqSeq) loading.value = false; }, 800);
  withTimeout(service.snapshot())
    .then(r => { if (my === reqSeq) snap.value = r; })
    .catch((e:any) => { if (my === reqSeq) error.value = e?.message || String(e); })
    .finally(() => { clearTimeout(lt); if (my === reqSeq) loading.value = false; });
};
const refreshWithModules = () => {
  const my = ++reqSeq; error.value = null;
  loading.value = true; const lt = setTimeout(() => { if (my === reqSeq) loading.value = false; }, 800);
  const mods: string[] = [];
  const s = m.value;
  if (s.cpu) mods.push('cpu');
  if (s.memory) mods.push('mem'); // 兼容后端规范化
  if (s.disk) mods.push('disk');
  if (s.network) mods.push('network');
  if (s.gpu) mods.push('gpu');
  if (s.sensor) mods.push('sensor');
  const p: SnapshotParams = { modules: mods.length ? mods : undefined };
  withTimeout(service.snapshot(p))
    .then(r => { if (my === reqSeq) snap.value = r; })
    .catch((e:any) => { if (my === reqSeq) error.value = e?.message || String(e); })
    .finally(() => { clearTimeout(lt); if (my === reqSeq) loading.value = false; });
};
onMounted(async () => {
  // 启动事件桥（在非 Tauri 环境内部会安全失败）
  try { await ensureEventBridge(); } catch { /* ignore */ }
  // 监听桥接与指标事件，推断连接状态
  const unsubs: Array<() => void> = [];
  try {
    const { listen } = await import('@tauri-apps/api/event');
    const on = async (evt: string, cb: (p: any) => void) => unsubs.push(await listen(evt, (e: any) => cb(e?.payload)));
    await on('bridge_handshake', () => { connected.value = true; lastEvent.value = 'handshake'; });
    await on('bridge_subscribe_ack', (p) => { connected.value = true; lastEvent.value = 'subscribe_ack'; });
    await on('bridge_disconnected', () => { connected.value = false; lastEvent.value = 'disconnected'; });
    await on('bridge_error', () => { connected.value = false; lastEvent.value = 'error'; });
    await on('metrics', () => { connected.value = true; lastEvent.value = 'metrics'; });
  } catch { /* 非 Tauri 环境 */ }
  // 首次拉取
  refresh();
  // 卸载时清理监听
  onUnmounted(() => { for (const u of unsubs) try { u(); } catch {} });
});
</script>
<style scoped>
.card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; }
.row { display: flex; gap: 12px; align-items: center; }
.row.small { margin-top: 8px; font-size: 12px; color: #555; }
.checks { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
.dot { width: 8px; height: 8px; border-radius: 50%; background: #ccc; display: inline-block; }
.dot.on { background: #2ecc71; }
label.toggle { user-select: none; }
pre.raw { background: #f6f8fa; padding: 8px; border-radius: 6px; margin-top: 8px; max-height: 360px; overflow: auto; }
button { padding: 6px 12px; }
.kv-list { max-height: 260px; overflow: auto; background: #fafafa; border-radius: 6px; padding: 6px; }
.kv-list strong { color: #333; }
.kv-list div { white-space: pre-wrap; word-break: break-all; }
</style>
