import { describe, expect, it } from 'vitest';
import { MemoryStorage } from '../src/index.js';
import type { OutboxEntry, Storage, StoredConversation, StoredMessage } from '../src/index.js';

function conversation(id: string, lastSeq: bigint, lastActivityAt: number): StoredConversation {
  return { id, title: id.toUpperCase(), lastSeq, lastActivityAt, unreadCount: 0, archived: false };
}

function message(id: string, conversationId: string, seq: bigint): StoredMessage {
  return {
    id,
    conversationId,
    seq,
    authorId: 'u1',
    sentAt: 1_725_540_000_000,
    body: `body ${id}`,
    state: 'sent',
  };
}

describe('MemoryStorage', () => {
  it('satisfies the Storage contract', async () => {
    const storage: Storage = new MemoryStorage();
    await storage.open();
    await storage.close();
    expect(storage.kv).toBeDefined();
  });

  it('stores key/value pairs and filters keys by prefix', async () => {
    const storage = new MemoryStorage();
    await storage.kv.set('session:token', 'abc');
    await storage.kv.set('session:device', 'dev-1');
    await storage.kv.set('flag:calls', 'on');

    expect(await storage.kv.get('session:token')).toBe('abc');
    expect(await storage.kv.get('missing')).toBeUndefined();
    expect((await storage.kv.keys('session:')).sort()).toEqual(['session:device', 'session:token']);
    await storage.kv.delete('flag:calls');
    expect(await storage.kv.get('flag:calls')).toBeUndefined();
  });

  it('orders conversations by activity and builds the resume cursor', async () => {
    const storage = new MemoryStorage();
    await storage.conversations.upsert(conversation('c1', 4n, 100));
    await storage.conversations.upsert(conversation('c2', 9n, 300));
    await storage.conversations.upsert({ ...conversation('c3', 1n, 200), archived: true });

    expect((await storage.conversations.list()).map((row) => row.id)).toEqual(['c2', 'c3', 'c1']);
    // Archived conversations do not take up one of the 50 `since` slots.
    expect(await storage.conversations.sinceCursor()).toEqual({ c2: 9n, c1: 4n });
    expect(await storage.conversations.sinceCursor(1)).toEqual({ c2: 9n });
  });

  it('returns messages in a seq window, ascending, and tracks the maximum seq', async () => {
    const storage = new MemoryStorage();
    for (let seq = 1n; seq <= 10n; seq += 1n) {
      await storage.messages.upsert(message(`m${seq.toString()}`, 'c1', seq));
    }
    await storage.messages.upsert(message('other', 'c2', 99n));

    const window = await storage.messages.list('c1', { fromSeq: 3n, toSeq: 6n });
    expect(window.map((row) => row.seq)).toEqual([3n, 4n, 5n, 6n]);
    expect(await storage.messages.maxSeq('c1')).toBe(10n);
    expect(await storage.messages.maxSeq('nope')).toBeUndefined();

    const tail = await storage.messages.list('c1', { fromSeq: 1n, toSeq: 10n, limit: 2 });
    expect(tail.map((row) => row.seq)).toEqual([9n, 10n]);
  });

  it('keeps the outbox in send order and supports partial updates', async () => {
    const storage = new MemoryStorage();
    const entry = (clientMsgId: string, createdAt: number): OutboxEntry => ({
      clientMsgId,
      conversationId: 'c1',
      body: clientMsgId,
      createdAt,
      attempts: 0,
    });
    await storage.outbox.enqueue(entry('b', 200));
    await storage.outbox.enqueue(entry('a', 100));

    expect((await storage.outbox.list()).map((row) => row.clientMsgId)).toEqual(['a', 'b']);
    await storage.outbox.update('a', { attempts: 3, lastError: 'UNAVAILABLE' });
    const [first] = await storage.outbox.list(1);
    expect(first?.attempts).toBe(3);
    expect(first?.lastError).toBe('UNAVAILABLE');

    await storage.outbox.remove('a');
    expect(await storage.outbox.list()).toHaveLength(1);
  });

  it('round-trips drafts', async () => {
    const storage = new MemoryStorage();
    await storage.drafts.set({ conversationId: 'c1', text: 'merhaba', updatedAt: 1 });
    expect((await storage.drafts.get('c1'))?.text).toBe('merhaba');
    expect(await storage.drafts.list()).toHaveLength(1);
    await storage.drafts.delete('c1');
    expect(await storage.drafts.get('c1')).toBeUndefined();
  });

  it('copies rows so callers cannot mutate the store behind its back', async () => {
    const storage = new MemoryStorage();
    const row = conversation('c1', 1n, 10);
    await storage.conversations.upsert(row);
    row.title = 'mutated after insert';
    const stored = await storage.conversations.get('c1');
    expect(stored?.title).toBe('C1');
    if (stored !== undefined) stored.title = 'mutated after read';
    expect((await storage.conversations.get('c1'))?.title).toBe('C1');
  });

  it('clears everything', async () => {
    const storage = new MemoryStorage();
    await storage.kv.set('k', 'v');
    await storage.conversations.upsert(conversation('c1', 1n, 1));
    await storage.messages.upsert(message('m1', 'c1', 1n));
    await storage.clear();

    expect(await storage.kv.keys()).toEqual([]);
    expect(await storage.conversations.list()).toEqual([]);
    expect(await storage.messages.maxSeq('c1')).toBeUndefined();
  });
});
