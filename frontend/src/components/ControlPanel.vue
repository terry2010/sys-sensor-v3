<template>
  <div class="card">
    <h3>Control Panel</h3>
    <div class="row">
      <button @click="onHello">hello()</button>
      <button @click="onSnapshot">snapshot()</button>
    </div>
    <div class="row">
      <button @click="onSetConfig">setConfig()</button>
      <button @click="onStart">start()</button>
      <button @click="onStop">stop()</button>
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
const log = (m: any) => logs.value.push(`[${new Date().toLocaleTimeString()}] ${typeof m === 'string' ? m : JSON.stringify(m)}`);

async function onHello() {
  try { const r = await api.hello(); log(r); } catch (e:any) { log(e?.message || e); }
}
async function onSnapshot() {
  try { const r = await api.snapshot(); log(r); } catch (e:any) { log(e?.message || e); }
}
async function onSetConfig() {
  try {
    const r = await api.setConfig?.({ base_interval_ms: 1000, persist: false });
    log(r ?? 'ok');
  } catch (e:any) { log(e?.message || e); }
}
async function onStart() {
  try {
    const r = await api.start?.({ modules: ['cpu','mem'] });
    log(r ?? 'ok');
  } catch (e:any) { log(e?.message || e); }
}
async function onStop() {
  try { const r = await api.stop?.({}); log(r ?? 'ok'); } catch (e:any) { log(e?.message || e); }
}
async function onBurst() {
  try {
    const r = await api.burstSubscribe?.({ modules: ['cpu'], interval_ms: 1000, ttl_ms: 5000 });
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
