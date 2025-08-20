<template>
  <div class="card">
    <h3>Control Panel</h3>
    <div class="row">
      <button @click="onHello">hello()</button>
      <button @click="onSnapshot">snapshot()</button>
    </div>
    <div class="row">
      <label>base_interval_ms: <input type="number" v-model.number="baseInterval" min="100" step="100" /></label>
      <label>persist: <input type="checkbox" v-model="persist" /></label>
      <button @click="onSetConfig">setConfig()</button>
    </div>
    <div class="row">
      <label>start modules: <input v-model="startModules" placeholder="cpu,mem" /></label>
      <button @click="onStart">start()</button>
      <button @click="onStop">stop()</button>
    </div>
    <div class="row">
      <label>burst modules: <input v-model="burstModules" placeholder="cpu" /></label>
      <label>interval_ms: <input type="number" v-model.number="burstInterval" min="100" step="100" /></label>
      <label>ttl_ms: <input type="number" v-model.number="burstTtl" min="1000" step="500" /></label>
      <button @click="onBurst">burstSubscribe()</button>
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

async function onHello() {
  try { const r = await api.hello(); log(r); } catch (e:any) { log(e?.message || e); }
}
async function onSnapshot() {
  try { const r = await api.snapshot(); log(r); } catch (e:any) { log(e?.message || e); }
}
async function onSetConfig() {
  try {
    const r = await api.setConfig?.({ base_interval_ms: baseInterval.value, persist: persist.value });
    log(r ?? 'ok');
  } catch (e:any) { log(e?.message || e); }
}
async function onStart() {
  try {
    const r = await api.start?.({ modules: startModules.value.split(',').map(s=>s.trim()).filter(Boolean) });
    log(r ?? 'ok');
  } catch (e:any) { log(e?.message || e); }
}
async function onStop() {
  try { const r = await api.stop?.({}); log(r ?? 'ok'); } catch (e:any) { log(e?.message || e); }
}
async function onBurst() {
  try {
    const r = await api.burstSubscribe?.({ modules: burstModules.value.split(',').map(s=>s.trim()).filter(Boolean), interval_ms: burstInterval.value, ttl_ms: burstTtl.value });
    log(r ?? 'ok');
  } catch (e:any) { log(e?.message || e); }
}
</script>

<style scoped>
.card { padding: 12px; border: 1px solid #eee; border-radius: 8px; }
.row { display: flex; gap: 8px; margin: 6px 0; flex-wrap: wrap; }
button { padding: 6px 10px; }
.log textarea { width: 100%; min-height: 120px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
</style>
