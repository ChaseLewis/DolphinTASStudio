const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { batchDirectory, runArguments, readResult } = require('../helpers');
test('paths with spaces remain separate executable arguments', () => {
  const config = path.resolve('a folder', 'experiment.tascsharp.json');
  const output = batchDirectory(config, new Date('2026-09-07T12:00:00Z'), 'test');
  assert.equal(path.dirname(path.dirname(output)), path.dirname(config));
  assert.deepEqual(runArguments(config, output), ['run-csharp', config, output]);
});
test('trial indices remain stable even when results finish in different order', () => {
  assert.deepEqual(readResult([{ Index: 2, Status: 'failed' }, { Index: 0, Status: 'completed' }]).map(r => r.Index), [2, 0]);
  assert.throws(() => readResult({})); assert.throws(() => readResult([{ Index: '1', Status: 'completed' }]));
});
