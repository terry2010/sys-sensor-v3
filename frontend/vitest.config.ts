import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    setupFiles: ['./src/api/__tests__/setup-tauri-mock.ts'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'text-summary', 'html'],
      reportsDirectory: '../artifacts/test-results/frontend/coverage',
      exclude: [
        'node_modules/**',
        'dist/**',
        '**/__tests__/**/fixtures/**',
      ],
    },
  },
});
