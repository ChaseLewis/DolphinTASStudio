const path = require('node:path');
function batchDirectory(configPath, now = new Date(), suffix = require('node:crypto').randomUUID().slice(0, 8)) {
  return path.join(path.dirname(configPath), '.runs', now.toISOString().replace(/[:.]/g, '-') + '-' + suffix);
}
function runArguments(configPath, output) { return ['run-csharp', configPath, output]; }
function readResult(value) {
  if (!Array.isArray(value)) throw new Error('Expected a results array.');
  for (const result of value) if (!Number.isInteger(result.Index) || typeof result.Status !== 'string') throw new Error('Invalid trial result.');
  return value;
}
module.exports = { batchDirectory, runArguments, readResult };
