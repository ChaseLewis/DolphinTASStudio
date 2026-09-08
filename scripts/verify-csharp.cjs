const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function main() {
  const source = path.resolve(process.argv[2]); const root = path.resolve(process.argv[3]);
  await fs.mkdir(root); // This verification never reuses or clears an existing batch.
  const worker = path.resolve('artifacts/prod/TasStudio.Worker.exe');
  const before = crypto.createHash('sha256').update(await fs.readFile(source)).digest('hex');
  const manifest = JSON.parse(await fs.readFile(source, 'utf8')); const state = manifest.States[0].Id;
  async function run(name, type, start, count, timeout = 90) {
    const file = path.join(root, name + '.tascsharp.json'); const output = path.join(root, name);
    await fs.writeFile(file, JSON.stringify({ Name: name, SourceProject: source,
      Project: path.resolve('tests/TasStudio.Experiment.Fixtures/TasStudio.Experiment.Fixtures.csproj'),
      AssemblyName: 'TasStudio.Experiment.Fixtures', TypeName: 'TasStudio.Experiment.Fixtures.' + type,
      Count: count, Parallelism: 2, Start: start, StateId: state, TimeoutSeconds: timeout,
      PrerollGroups: type === 'NativeTrial' ? 2 : 0, Parameters: {} }));
    const child = spawn(worker, ['run-csharp', file, output], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let consoleText = ''; child.stdout.on('data', data => { consoleText += data; }); child.stderr.on('data', data => { consoleText += data; });
    const finished = new Promise((resolve, reject) => { child.on('error', reject); child.on('close', resolve); });
    if (type === 'CancelRuntime') {
      const end = Date.now() + 90000; let ready = false;
      while (Date.now() < end) {
        const text = await fs.readFile(path.join(output, 'run-00001', 'output.jsonl'), 'utf8').catch(() => '');
        if (text.includes('Waiting')) { ready = true; break; }
        if (child.exitCode !== null) break;
        await pause(50);
      }
      await fs.mkdir(output, { recursive: true }); await fs.writeFile(path.join(output, 'cancel'), 'cancel');
      assert.ok(ready, consoleText);
    }
    const code = await finished;
    await fs.writeFile(path.join(root, name + '.log'), consoleText);
    const results = JSON.parse(await fs.readFile(path.join(output, 'results.json'), 'utf8'));
    if (type === 'NativeTrial') {
      assert.equal(code, 0, consoleText);
      for (const result of results) {
        assert.equal(result.Status, 'completed', JSON.stringify(result));
        assert.equal(result.Value.Index, result.Index); assert.equal(result.Value.Count, count);
        assert.equal(result.Value.Utc, manifest.Environment.Configuration.StartUtcSeconds + (start === 'Boot' ? result.Index : 0));
        assert.equal(result.Value.StartGroup, start === 'Boot' ? 2 : Number(manifest.States[0].Position) + 2);
        assert.equal(result.Value.FinalGroup, result.Value.StartGroup + 1); assert.equal(result.Value.MovieLength, 30);
        await fs.access(result.ProjectPath);
      }
    } else {
      assert.equal(results[0].Status, type === 'InitializationLoop' ? 'timed out' : 'cancelled', JSON.stringify(results));
      if (type === 'CancelRuntime') assert.ok(results[0].ProjectPath);
      else assert.equal(results[0].ProjectPath, null);
    }
    console.log(name + ': ' + results.map(r => r.Status).join(', ')); return results;
  }
  const results = [
    ...await run('Boot', 'NativeTrial', 'Boot', 3),
    ...await run('State', 'NativeTrial', 'SaveState', 3),
    ...await run('Cancellation', 'CancelRuntime', 'SaveState', 1),
    ...await run('InitializationTimeout', 'InitializationLoop', 'Boot', 1, 2)
  ];
  assert.equal(crypto.createHash('sha256').update(await fs.readFile(source)).digest('hex'), before);
  await fs.writeFile(path.join(root, 'verification.json'), JSON.stringify({ sourceUnchanged: true, results }, null, 2));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
