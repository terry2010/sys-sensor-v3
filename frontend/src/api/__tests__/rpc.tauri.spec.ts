import { describe, it, expect } from 'vitest';
import { ensureEventBridge } from '../../api/rpc.tauri';

// Ensure calling in pure Web env (no __TAURI__) does not throw
describe('ensureEventBridge', () => {
  it('should not throw when __TAURI__ is absent', async () => {
    await ensureEventBridge();
    expect(true).toBe(true);
  });
});
