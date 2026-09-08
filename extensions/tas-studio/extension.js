const vscode = require('vscode');
const path = require('node:path');
const fs = require('node:fs/promises');
const { spawn } = require('node:child_process');
const { batchDirectory, runArguments, readResult } = require('./helpers');
const active = new Map();
let output, changed;

function workerFor(uri) {
  const worker = vscode.workspace.getConfiguration('tasStudio', uri).get('workerPath');
  if (!worker || !path.isAbsolute(worker)) throw new Error('Set tasStudio.workerPath to the absolute path of TasStudio.Worker.exe.');
  return worker;
}
function invoke(executable, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, { shell: false, windowsHide: true });
    child.stdout.on('data', data => output.append(data.toString()));
    child.stderr.on('data', data => output.append(data.toString()));
    child.once('error', reject);
    child.once('close', code => resolve(code));
  });
}
async function pickExperiment(item) {
  if (item instanceof vscode.Uri) return item;
  if (item?.uri) return item.uri;
  const editor = vscode.window.activeTextEditor;
  if (editor?.document.uri.fsPath.endsWith('.tascsharp.json')) return editor.document.uri;
  const files = await vscode.workspace.findFiles('**/*.tascsharp.json', '**/{bin,obj,.runs}/**');
  const selected = await vscode.window.showQuickPick(files.map(uri => ({ label: vscode.workspace.asRelativePath(uri), uri })), { placeHolder: 'Choose an experiment' });
  return selected?.uri;
}
async function run(item) {
  const uri = await pickExperiment(item); if (!uri) return;
  const worker = workerFor(uri);
  if (active.has(uri.fsPath)) throw new Error('This experiment is already running.');
  if (!await vscode.workspace.saveAll(false)) throw new Error('Save your script files before running.');
  const directory = batchDirectory(uri.fsPath);
  await launchBatch(uri.fsPath, worker, directory, runArguments(uri.fsPath, directory));
}
async function resume(item) {
  let directory;
  if (item?.kind === 'batch') directory = path.dirname(item.uri.fsPath);
  else {
    const files = await vscode.workspace.findFiles('**/.runs/*/experiment.json', null, 1000);
    const selected = await vscode.window.showQuickPick(files.sort((a, b) => b.fsPath.localeCompare(a.fsPath))
      .map(uri => ({ label: vscode.workspace.asRelativePath(path.dirname(uri.fsPath)), uri })),
      { placeHolder: 'Resume a saved batch using its original script and movie' });
    if (!selected) return;
    directory = path.dirname(selected.uri.fsPath);
  }
  await launchBatch(directory, workerFor(vscode.Uri.file(directory)), directory, ['resume-csharp', directory]);
}
async function launchBatch(key, worker, directory, args) {
  if ([...active.values()].some(run => run.directory === directory)) throw new Error('This batch is already running.');
  output.show(true); output.appendLine(args[0] + '\nWorker: ' + worker + '\nBatch: ' + directory);
  const pending = invoke(worker, args);
  active.set(key, { directory, pending }); changed.fire();
  try {
    const code = await pending;
    output.appendLine('Batch finished with exit code ' + code);
    if (code !== 0) vscode.window.showWarningMessage('Experiment stopped or failed. See TAS Experiments output and trial logs.');
  } finally { active.delete(key); changed.fire(); }
}
async function cancelAll() {
  for (const { directory } of active.values()) {
    await fs.mkdir(directory, { recursive: true });
    await fs.writeFile(path.join(directory, 'cancel'), 'cancel');
  }
}
class Experiments {
  onDidChangeTreeData = changed.event;
  getTreeItem(node) {
    const item = new vscode.TreeItem(node.label, node.kind === 'batch' ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
    item.contextValue = node.kind;
    if (node.kind === 'experiment') {
      item.description = active.has(node.uri.fsPath) ? 'Running' : '';
      item.resourceUri = node.uri; item.command = { command: 'vscode.open', title: 'Edit configuration', arguments: [node.uri] };
    }
    if (node.result) { item.description = node.result.Status; item.tooltip = node.result.Error || node.result.Name; }
    return item;
  }
  async getChildren(node) {
    if (node?.kind === 'batch') {
      const text = await fs.readFile(node.uri.fsPath, 'utf8').catch(error => { if (error.code === 'ENOENT') return '[]'; throw error; });
      const results = readResult(JSON.parse(text));
      return Promise.all(results.map(async result => {
        const trial = path.join(path.dirname(node.uri.fsPath), 'run-' + String(result.Index + 1).padStart(5, '0'));
        const job = await fs.readFile(path.join(trial, 'job.json'), 'utf8').then(JSON.parse)
          .catch(error => { if (error.code === 'ENOENT') return null; throw error; });
        const kind = result.ProjectPath ? 'result' : result.Status === 'completed' ? 'completedResult' :
          result.Status === 'cancelled' ? 'cancelledResult' : 'failedResult';
        return { kind, label: '#' + result.Index + ' ' + result.Name,
          result, uri: node.uri, directory: result.ProjectPath ? path.dirname(result.ProjectPath) : job?.OutputDirectory || trial };
      }));
    }
    if (node) return [];
    const scripts = await vscode.workspace.findFiles('**/*.tascsharp.json', '**/{bin,obj,.runs}/**');
    const batches = await vscode.workspace.findFiles('**/.runs/*/experiment.json', null, 1000);
    return [
      ...scripts.map(uri => ({ kind: 'experiment', label: vscode.workspace.asRelativePath(uri), uri })),
      ...batches.sort((a, b) => b.fsPath.localeCompare(a.fsPath)).map(uri => ({ kind: 'batch', label: path.basename(path.dirname(uri.fsPath)),
        uri: vscode.Uri.file(path.join(path.dirname(uri.fsPath), 'results.json')) }))
    ];
  }
}
function activate(context) {
  changed = new vscode.EventEmitter(); output = vscode.window.createOutputChannel('TAS Experiments');
  context.subscriptions.push(changed, output, vscode.window.registerTreeDataProvider('tasStudio.experiments', new Experiments()));
  function command(name, action) {
    context.subscriptions.push(vscode.commands.registerCommand(name, async (...args) => {
      if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before building or running C# code.');
      try { return await action(...args); } catch (error) { output.appendLine(String(error)); vscode.window.showErrorMessage(String(error)); throw error; }
    }));
  }
  command('tasStudio.run', run);
  command('tasStudio.resume', resume);
  command('tasStudio.cancel', cancelAll);
  command('tasStudio.refresh', () => changed.fire());
  command('tasStudio.openLog', async node => {
    if (!node?.directory) return;
    const structured = path.join(node.directory, 'output.jsonl');
    const fallback = path.join(node.directory, 'error.log');
    const log = await fs.stat(structured).then(info => info.size > 0 ? structured : fallback).catch(() => fallback);
    await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(log));
  });
  command('tasStudio.openResult', async node => {
    if (!node?.result?.ProjectPath) return;
    const app = path.join(path.dirname(workerFor(node.uri)), 'TasStudio.App.exe');
    const child = spawn(app, ['--project', node.result.ProjectPath], { shell: false, windowsHide: false, detached: true, stdio: 'ignore' });
    child.once('error', error => vscode.window.showErrorMessage(String(error))); child.unref();
  });
  command('tasStudio.new', async () => {
    const source = (await vscode.window.showOpenDialog({ canSelectMany: false, filters: { 'Studio project': ['tasproj'] }, title: 'Choose a saved TAS project' }))?.[0];
    if (!source) return;
    const worker = workerFor(source);
    const parent = (await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, title: 'Choose parent directory for the new scripting workspace' }))?.[0];
    if (!parent) return;
    const name = await vscode.window.showInputBox({ prompt: 'New workspace folder name', value: 'TAS Experiments', validateInput: value => !value || /[<>:"/\\|?*]/.test(value) || value === '.' || value === '..' ? 'Enter a folder name.' : undefined });
    if (!name) return;
    const directory = path.join(parent.fsPath, name);
    if (await invoke(worker, ['new-csharp', source.fsPath, directory]) !== 0) throw new Error('Workspace creation failed. See TAS Experiments output.');
    await vscode.commands.executeCommand('vscode.openFolder', vscode.Uri.file(directory), { forceNewWindow: true });
  });
  const watcher = vscode.workspace.createFileSystemWatcher('**/{*.tascsharp.json,experiment.json,results.json}');
  watcher.onDidCreate(() => changed.fire()); watcher.onDidChange(() => changed.fire()); watcher.onDidDelete(() => changed.fire());
  context.subscriptions.push(watcher);
}
async function deactivate() {
  await cancelAll(); await Promise.allSettled([...active.values()].map(run => run.pending));
}
module.exports = { activate, deactivate };
