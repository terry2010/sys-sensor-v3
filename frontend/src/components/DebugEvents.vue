<template>
  <div class="card">
    <h3>事件流 (最近 {{ items.length }} 条)</h3>
    <div class="filters">
      <div class="left">
        <label v-for="t in types" :key="t" class="chk">
          <input type="checkbox" :value="t" v-model="selected" />
          <span>{{ t }}</span>
        </label>
      </div>
      <div class="right">
        <button @click="toggleAll()">{{ isAll ? '全不选' : '全选' }}</button>
        <button @click="onlyState()">只看 state</button>
      </div>
    </div>
    <div class="list">
      <div v-for="(ev, idx) in filtered" :key="idx" class="row" :class="ev.type">
        <span class="ts">{{ fmt(ev.ts) }}</span>
        <span class="type">{{ ev.type }}</span>
        <pre class="payload">{{ short(ev.payload) }}</pre>
      </div>
    </div>
    <div class="ops">
      <button @click="clear()">清空</button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { storeToRefs } from 'pinia';
import { useEventsStore } from '../stores/events';
const events = useEventsStore();
const { items } = storeToRefs(events);
const reversed = computed(() => [...items.value].reverse());
// 可过滤的事件类型
const types = ['metrics','state','bridge_rx','bridge_error','info','warn','error'] as const;
const LS_KEY = 'debug_events_selected_types';
const selected = ref<string[]>([...types]);
const isAll = computed(() => selected.value.length === types.length);
const filtered = computed(() => reversed.value.filter(ev => selected.value.includes(ev.type as any)));
function fmt(ts: number) { const d = new Date(ts); return d.toLocaleTimeString(); }
function short(p: any) {
  try { return JSON.stringify(p); } catch { return String(p); }
}
function clear() { events.clear(); }
function toggleAll() { selected.value = isAll.value ? [] : [...types]; }
function onlyState() { selected.value = ['state']; }

onMounted(() => {
  try {
    const saved = JSON.parse(localStorage.getItem(LS_KEY) || '[]');
    if (Array.isArray(saved) && saved.length > 0) {
      selected.value = saved.filter((x: any) => (types as any).includes(x));
    }
  } catch { /* ignore */ }
});
watch(selected, (v) => {
  try { localStorage.setItem(LS_KEY, JSON.stringify(v)); } catch { /* ignore */ }
}, { deep: true });
</script>

<style scoped>
.card { padding: 12px; border: 1px solid #eee; border-radius: 8px; background: #fff; }
.filters { display: flex; justify-content: space-between; align-items: center; gap: 8px; margin-bottom: 8px; }
.filters .left { display: flex; flex-wrap: wrap; gap: 8px 12px; }
.filters .chk { font-size: 12px; display: inline-flex; align-items: center; gap: 4px; }
.list { max-height: 220px; overflow: auto; font-size: 12px; }
.row { display: grid; grid-template-columns: 70px 90px 1fr; gap: 8px; padding: 4px 0; border-bottom: 1px dashed #eee; }
.row:last-child { border-bottom: none; }
.ts { color: #666; }
.type { font-weight: 600; }
.payload { margin: 0; white-space: pre-wrap; word-break: break-word; }
.row.metrics .type { color: #237804; }
.row.state .type { color: #0050b3; }
.row.bridge_error .type { color: #a8071a; }
</style>
