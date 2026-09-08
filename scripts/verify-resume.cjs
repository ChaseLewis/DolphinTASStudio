const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { DatabaseSync } = require('node:sqlite');

async function main() {
  const source = path.resolve(process.argv[2]);
  const root = path.resolve(process.argv[3]);
  await fs.mkdir(root); // Never reuse an existing verification directory.
  const hash = async file => crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex');
  const sourceHash = await hash(source);
  const original = JSON.parse(await fs.readFile(source, 'utf8'));
  const config = path.join(root, 'experiment.tascsharp.json');
  const batch = path.join(root, 'batch');
  const worker = path.resolve(process.argv[4] || 'artifacts/prod/TasStudio.Worker.exe');
  await fs.writeFile(config, JSON.stringify({ Name: 'Resume verification', SourceProject: source,
    Project: path.resolve('tests/TasStudio.Experiment.Fixtures/TasStudio.Experiment.Fixtures.csproj'),
    AssemblyName: 'TasStudio.Experiment.Fixtures', TypeName: 'TasStudio.Experiment.Fixtures.ResumeTrial',
    Count: 4, Parallelism: 1, TimeoutSeconds: 90, Start: 'Boot', Headless: true, Parameters: {} }));

  async function invoke(args, stopAfterFirst = false) {
    const child = spawn(worker, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let text = '', stopping;
    child.stdout.on('data', data => {
      text += data;
      if (stopAfterFirst && !stopping && text.includes('Trial 0: completed'))
        stopping = fs.writeFile(path.join(batch, 'cancel'), 'cancel');
    });
    child.stderr.on('data', data => { text += data; });
    const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('close', resolve); });
    await stopping;
    return { code, text };
  }
  const initial = await invoke(['run-csharp', config, batch], true);
  await fs.writeFile(path.join(root, 'initial.log'), initial.text);
  const before = JSON.parse(await fs.readFile(path.join(batch, 'results.json'), 'utf8'));
  assert.equal(before[0].Status, 'completed', initial.text);
  assert.ok(before.some(r => r.Status === 'cancelled'), initial.text);
  assert.ok(before.length < 4, 'Cancellation must not generate pending trial results.');
  assert.ok(!(await fs.readdir(batch)).some(name => name.startsWith('run-')), 'Committed success/cancelled folders should be removed.');
  const readReceipt = () => {
    const db = new DatabaseSync(path.join(batch, 'results.sqlite'), { readOnly: true });
    try { return db.prepare('SELECT result_json FROM trials WHERE trial_index=0').get().result_json; }
    finally { db.close(); }
  };
  const receipt = readReceipt();

  // Resume must ignore current authoring config and the old publish output path.
  await fs.writeFile(config, JSON.stringify({ Count: 999, Project: 'does-not-exist.csproj' }));
  await fs.rename(path.join(batch, 'build'), path.join(batch, 'unused-build'));
  const resumed = await invoke(['resume-csharp', batch]);
  await fs.writeFile(path.join(root, 'resume.log'), resumed.text);
  assert.equal(resumed.code, 1, resumed.text); // Trial 3 deliberately fails.
  assert.ok(!resumed.text.includes('Starting 1/4'), resumed.text);
  const after = JSON.parse(await fs.readFile(path.join(batch, 'results.json'), 'utf8'));
  assert.deepEqual(after.map(r => r.Status), ['completed', 'completed', 'completed', 'failed']);
  assert.deepEqual(after.map(r => r.Index), [0, 1, 2, 3]);
  for (const result of after.filter(r => r.Status === 'completed')) {
    assert.equal(result.Value.Index, result.Index);
    assert.equal(result.Value.Count, 4);
    assert.equal(result.Value.Utc, original.Environment.Configuration.StartUtcSeconds + result.Index);
  }
  assert.equal(readReceipt(), receipt);
  assert.deepEqual((await fs.readdir(batch)).filter(name => name.startsWith('run-')), ['run-00004']);
  assert.ok(after[3].ProjectPath);
  await fs.access(after[3].ProjectPath);
  assert.ok(after.slice(0, 3).every(r => r.ProjectPath === null));
  assert.equal(await hash(source), sourceHash);
  const db = new DatabaseSync(path.join(batch, 'results.sqlite'), { readOnly: true });
  const rows = db.prepare('SELECT trial_index, "Index", "Count", Utc FROM results ORDER BY trial_index').all();
  assert.deepEqual(rows.map(r => r.trial_index), [0, 1, 2]);
  assert.equal(db.prepare('SELECT COUNT(*) AS count FROM trials').get().count, 4);
  db.close();
  const repeat = await invoke(['resume-csharp', batch]);
  assert.equal(repeat.code, 1, repeat.text);
  assert.ok(!repeat.text.includes('Starting'), repeat.text);
  await fs.writeFile(path.join(root, 'verification.json'), JSON.stringify({ before, after, rows,
    sourceUnchanged: true, completedReceiptUnchanged: true, repeatedResumeNoWorkers: true, onlyFailedFolderRemains: true }, null, 2));
  console.log('Native retention/resume passed: only failed trial folder remains; successful results resume from SQLite, original indices/UTC preserved, cancellation leaves unstarted trials pending.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
