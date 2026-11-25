const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const { EventEmitter } = require('node:events');

test('interrupted batches remain visible and resume invokes saved batch without saving or rebuilding source', async () => {
  const batch = path.resolve('project/.runs/interrupted');
  const files = new Map();
  const config = path.resolve('project/experiment.tascsharp.json');
  const identity = path.resolve('project/experiment.identity.json');
  files.set(identity, JSON.stringify({ Name: 'Boss RNG' }));
  files.set(config, JSON.stringify({ DisplayName: 'Boss RNG', Name: 'Last successful run: zero-delay winner' }));
  files.set(path.join(batch, 'experiment.json'), JSON.stringify({ Name: 'Old successful run' }));
  const commands = new Map();
  const invocations = [];
  let provider, saveCalls = 0;
  class Uri { constructor(file) { this.fsPath = file; } static file(file) { return new Uri(file); } }
  class Event { event() {} fire() {} }
  const vscode = {
    Uri, EventEmitter: Event,
    TreeItem: class { constructor(label) { this.label = label; } },
    TreeItemCollapsibleState: { Collapsed: 1, None: 0 },
    commands: { registerCommand: (name, action) => { commands.set(name, action); return {}; } },
    workspace: {
      isTrusted: true,
      getConfiguration: () => ({ get: () => path.resolve('TasStudio.Worker.exe') }),
      saveAll: async () => { saveCalls++; return true; },
      asRelativePath: value => value.fsPath || value,
      findFiles: async pattern => pattern.includes('experiment.json') ? [Uri.file(path.join(batch, 'experiment.json'))] : [],
      createFileSystemWatcher: () => ({ onDidCreate() {}, onDidChange() {}, onDidDelete() {} })
    },
    window: {
      createOutputChannel: () => ({ append() {}, appendLine() {}, show() {} }),
      registerTreeDataProvider: (_, value) => { provider = value; return {}; },
      showQuickPick: async choices => choices[0],
      showWarningMessage: message => assert.fail(message),
      showErrorMessage: message => assert.fail(message)
    }
  };
  const module = { exports: {} };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../extension.js'), 'utf8'), {
    module, require: name => {
      if (name === 'vscode') return vscode;
      if (name === './helpers') return require('../helpers');
      if (name === 'node:fs/promises') return {
        readFile: async file => { if (files.has(file)) return files.get(file); throw Object.assign(new Error('missing'), { code: 'ENOENT' }); }
      };
      if (name === 'node:child_process') return { spawn: (exe, args, options) => {
        invocations.push({ exe, args: Array.from(args), options });
        const child = new EventEmitter(); child.stdout = new EventEmitter(); child.stderr = new EventEmitter();
        queueMicrotask(() => child.emit('close', 0)); return child;
      } };
      return require(name);
    }
  });
  module.exports.activate({ subscriptions: [] });
  const nodes = await provider.getChildren();
  assert.equal(nodes.length, 1);
  assert.equal(nodes[0].kind, 'batch');
  assert.equal(nodes[0].label, 'Boss RNG');
  assert.equal(provider.getTreeItem(nodes[0]).description, 'Run: interrupted');
  files.set(config, JSON.stringify({ DisplayName: 'Boss RNG', Name: 'Next sweep: 200 trials' }));
  assert.equal((await provider.getChildren())[0].label, 'Boss RNG');
  files.set(config, JSON.stringify({ Name: 'Last successful run' }));
  files.delete(identity);
  assert.equal((await provider.getChildren())[0].label, 'project');
  files.set(identity, JSON.stringify({ Name: 'Boss RNG' }));
  files.set(config, JSON.stringify({ DisplayName: 'Latest batch', Name: 'Latest batch' }));
  assert.equal((await provider.getChildren(nodes[0])).length, 0); // No final summary after a crash.
  await commands.get('tasStudio.resume')(nodes[0]);
  await commands.get('tasStudio.resume')(); // Command Palette picker.
  assert.equal(saveCalls, 0);
  assert.equal(invocations.length, 2);
  for (const invocation of invocations) {
    assert.deepEqual(invocation.args, ['resume-csharp', batch]);
    assert.equal(invocation.options.shell, false);
  }
  const attempt = path.join(batch, 'run-00001/attempts/retry');
  files.set(path.join(batch, 'results.json'), JSON.stringify([{ Index: 0, Name: 'Trial 0', Status: 'failed' }]));
  files.set(path.join(batch, 'run-00001/job.json'), JSON.stringify({ OutputDirectory: attempt }));
  assert.equal((await provider.getChildren(nodes[0]))[0].directory, attempt);
  files.delete(path.join(batch, 'results.json'));
  files.set(path.join(batch, 'results.json.gz'), require('node:zlib').gzipSync(JSON.stringify([{ Index: 0, Name: 'Compressed trial', Status: 'completed' }])));
  const compressed = (await provider.getChildren(nodes[0]))[0];
  assert.equal(compressed.label, '#0 Compressed trial');
  assert.equal(compressed.kind, 'completedResult');

  const nested = path.resolve('project/.runs/studio-run/batch/experiment.json');
  files.set(nested, JSON.stringify({ Name: 'Another winning run' }));
  vscode.workspace.findFiles = async pattern => pattern.includes('experiment.json') ? [Uri.file(nested)] : [Uri.file(config)];
  const current = await provider.getChildren();
  assert.equal(current.length, 2);
  assert.deepEqual(Array.from(current, node => node.label), ['Boss RNG', 'Boss RNG']);
  assert.equal(current[0].kind, 'experiment');
  files.set(identity, JSON.stringify({ name: ' Renamed search ' }));
  assert.deepEqual(Array.from(await provider.getChildren(), node => node.label), ['Renamed search', 'Renamed search']);
  files.set(identity, JSON.stringify({ Name: ' ' }));
  assert.deepEqual(Array.from(await provider.getChildren(), node => node.label), ['project', 'project']);
  const detached = path.resolve('exported-batch/experiment.json');
  files.set(detached, JSON.stringify({ Name: 'Batch must not become experiment', DisplayName: 'Batch must not become experiment' }));
  vscode.workspace.findFiles = async pattern => pattern.includes('experiment.json') ? [Uri.file(detached)] : [];
  assert.equal((await provider.getChildren())[0].label, 'Experiment');
  files.set(path.resolve('exported-batch/experiment.identity.json'), JSON.stringify({ Name: 'Original experiment' }));
  assert.equal((await provider.getChildren())[0].label, 'Original experiment');
});
