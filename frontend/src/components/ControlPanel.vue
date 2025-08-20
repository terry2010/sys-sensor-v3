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
    <div class="log">
      <textarea :value="logs.join('\n')" readonly></textarea>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { service as api } from '../api/service';

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
</script>

<style scoped>
.card { padding: 12px; border: 1px solid #eee; border-radius: 8px; }
.row { display: flex; gap: 8px; margin: 6px 0; flex-wrap: wrap; }
button { padding: 6px 10px; }
.log textarea { width: 100%; min-height: 120px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
</style>
