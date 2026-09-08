const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const { EventEmitter } = require('node:events');

test('interrupted batches remain visible and resume invokes saved batch without saving or rebuilding source', async () => {
  const batch = path.resolve('project/.runs/interrupted');
  const files = new Map();
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
});
