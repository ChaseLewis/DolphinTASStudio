const vscode = require('vscode');
const path = require('node:path');
const fs = require('node:fs/promises');
const { gunzipSync } = require('node:zlib');
const { spawn } = require('node:child_process');
const { batchDirectory, runArguments, readResult } = require('./helpers');
const active = new Map();
let output, changed;

async function configuredName(file, field) {
  try {
    const config = JSON.parse(await fs.readFile(file, 'utf8'));
    const name = Object.entries(config).find(([key]) => key.toLowerCase() === field.toLowerCase())?.[1];
    if (typeof name === 'string' && name.trim()) return name.trim();
  } catch { /* Keep incomplete or older runs visible. */ }
}
async function experimentName(uri) {
  const folder = path.dirname(uri.fsPath);
  // Run configs are replaceable. Only the separate experiment identity can rename a recipe.
  for (let parent = folder; path.dirname(parent) !== parent; parent = path.dirname(parent)) {
    if (path.basename(parent).toLowerCase() !== '.runs') continue;
    const experiment = path.dirname(parent);
    return await configuredName(path.join(experiment, 'experiment.identity.json'), 'Name') || path.basename(experiment);
  }
  if (uri.fsPath.endsWith('.tascsharp.json'))
    return await configuredName(path.join(folder, 'experiment.identity.json'), 'Name') || path.basename(folder);
  // Detached batches use a separate identity snapshot, never fields in run configs.
  const identities = [path.join(folder, 'experiment.identity.json')];
  if (path.basename(folder).toLowerCase() === 'batch') identities.push(path.join(path.dirname(folder), 'experiment.identity.json'));
  for (const identity of identities) {
    const name = await configuredName(identity, 'Name');
    if (name) return name;
  }
  return 'Experiment';
}

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
  const selected = await vscode.window.showQuickPick(await Promise.all(files.map(async uri => ({
    label: await experimentName(uri), description: vscode.workspace.asRelativePath(uri), uri }))), { placeHolder: 'Choose an experiment' });
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
    const selected = await vscode.window.showQuickPick(await Promise.all(files.sort((a, b) => b.fsPath.localeCompare(a.fsPath))
      .map(async uri => ({ label: await experimentName(uri), description: vscode.workspace.asRelativePath(path.dirname(uri.fsPath)), uri }))),
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
    item.description = node.description;
    item.tooltip = node.uri.fsPath;
    if (node.kind === 'experiment') {
      item.description = (active.has(node.uri.fsPath) ? 'Running · ' : '') + node.description;
      item.resourceUri = node.uri; item.command = { command: 'vscode.open', title: 'Edit configuration', arguments: [node.uri] };
    }
    if (node.result) { item.description = node.result.Status; item.tooltip = node.result.Error || node.result.Name; }
    return item;
  }
  async getChildren(node) {
    if (node?.kind === 'batch') {
      const text = await fs.readFile(node.uri.fsPath + '.gz').then(data => gunzipSync(data).toString('utf8')).catch(async error => {
        if (error.code !== 'ENOENT') throw error;
        return fs.readFile(node.uri.fsPath, 'utf8').catch(plainError => { if (plainError.code === 'ENOENT') return '[]'; throw plainError; });
      });
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
      ...await Promise.all(scripts.map(async uri => ({ kind: 'experiment', label: await experimentName(uri), description: vscode.workspace.asRelativePath(uri), uri }))),
      ...await Promise.all(batches.sort((a, b) => b.fsPath.localeCompare(a.fsPath)).map(async uri => ({ kind: 'batch', label: await experimentName(uri),
        description: 'Run: ' + path.basename(path.dirname(uri.fsPath)), uri: vscode.Uri.file(path.join(path.dirname(uri.fsPath), 'results.json')) })))
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
  const watcher = vscode.workspace.createFileSystemWatcher('**/{*.tascsharp.json,experiment.identity.json,experiment.json,results.json,results.json.gz}');
  watcher.onDidCreate(() => changed.fire()); watcher.onDidChange(() => changed.fire()); watcher.onDidDelete(() => changed.fire());
  context.subscriptions.push(watcher);
}
async function deactivate() {
  await cancelAll(); await Promise.allSettled([...active.values()].map(run => run.pending));
}
module.exports = { activate, deactivate };
