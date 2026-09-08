const vscode = require('vscode');
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
exports.run = async function () {
  const root = vscode.workspace.workspaceFolders[0].uri;
  const extension = vscode.extensions.getExtension('tas-studio.experiments');
  assert.ok(extension); await extension.activate();
  const commands = await vscode.commands.getCommands();
  assert.ok(commands.includes('tasStudio.run'));
  await vscode.commands.executeCommand('tasStudio.run', vscode.Uri.joinPath(root, 'experiment.tascsharp.json'));
  const batches = await fs.readdir(path.join(root.fsPath, '.runs')); assert.equal(batches.length, 1);
  const results = JSON.parse(await fs.readFile(path.join(root.fsPath, '.runs', batches[0], 'results.json'), 'utf8'));
  assert.equal(results.length, 1); assert.equal(results[0].Status, 'completed', JSON.stringify(results));
  await vscode.commands.executeCommand('tasStudio.refresh');
  await vscode.commands.executeCommand('tasStudio.cancel'); // No pending jobs is a harmless no-op.
  await fs.writeFile(path.join(root.fsPath, 'host-verification.json'), JSON.stringify({ activated: true, commandsRegistered: true, results }, null, 2));
};
