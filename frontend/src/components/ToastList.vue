<template>
  <div class="toasts">
    <div v-for="t in items" :key="t.id" class="toast" :class="t.type">
      <span class="text">{{ t.text }}</span>
      <button class="x" @click="remove(t.id)">×</button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { storeToRefs } from 'pinia';
import { useToastStore } from '../stores/toast';
const toast = useToastStore();
const { items } = storeToRefs(toast);
const remove = (id: number) => toast.remove(id);
</script>

<style scoped>
.toasts { position: fixed; top: 12px; right: 12px; display: flex; flex-direction: column; gap: 8px; z-index: 9999; }
.toast { display: flex; align-items: center; gap: 8px; padding: 8px 10px; border-radius: 6px; border: 1px solid #ddd; background: #fff; box-shadow: 0 2px 8px rgba(0,0,0,.08); font-size: 12px; }
.toast.info { border-color: #cce1ff; background: #f0f7ff; color: #084298; }
.toast.warn { border-color: #ffe58f; background: #fffbe6; color: #ad6800; }
.toast.error { border-color: #ffccc7; background: #fff2f0; color: #a8071a; }
.toast.success { border-color: #b7eb8f; background: #f6ffed; color: #237804; }
.x { background: transparent; border: none; cursor: pointer; font-size: 14px; line-height: 1; color: inherit; }
.text { white-space: pre-wrap; }
</style>
