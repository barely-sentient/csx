"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const vscode = __importStar(require("vscode"));
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const child_process_1 = require("child_process");
let client;
let output;
let status;
let starting = false;
let extensionPath = '';
const logFile = path.join(os.tmpdir(), 'csx-extension.log');
function log(message) {
    const line = `[${new Date().toISOString()}] ${message}`;
    try {
        output?.appendLine(line);
    }
    catch { /* ignore */ }
    try {
        fs.appendFileSync(logFile, line + os.EOL);
    }
    catch { /* ignore */ }
}
function commandExists(command) {
    try {
        if (process.platform === 'win32') {
            (0, child_process_1.execFileSync)('where.exe', [command], { stdio: 'ignore' });
        }
        else {
            (0, child_process_1.execFileSync)('which', [command], { stdio: 'ignore' });
        }
        return true;
    }
    catch {
        return false;
    }
}
function firstExisting(candidates) {
    for (const candidate of candidates) {
        try {
            if (candidate && fs.existsSync(candidate)) {
                return candidate;
            }
        }
        catch { /* ignore */ }
    }
    return undefined;
}
function findBuiltCli(projectFile) {
    const projectDir = path.dirname(projectFile);
    const assemblyName = path.basename(projectFile, path.extname(projectFile));
    const binDir = path.join(projectDir, 'bin');
    if (!fs.existsSync(binDir)) {
        return undefined;
    }
    for (const configuration of ['Debug', 'Release']) {
        const configurationDir = path.join(binDir, configuration);
        if (!fs.existsSync(configurationDir)) {
            continue;
        }
        for (const framework of fs.readdirSync(configurationDir)) {
            if (!framework.startsWith('net')) {
                continue;
            }
            const dir = path.join(configurationDir, framework);
            const exe = path.join(dir, `${assemblyName}.exe`);
            if (fs.existsSync(exe)) {
                return exe;
            }
            const dll = path.join(dir, `${assemblyName}.dll`);
            if (fs.existsSync(dll)) {
                return dll;
            }
        }
    }
    return undefined;
}
function buildProject(projectFile) {
    log(`building ${projectFile}`);
    try {
        (0, child_process_1.execFileSync)('dotnet', ['build', projectFile, '-c', 'Debug', '-v', 'quiet', '--nologo'], {
            stdio: 'ignore',
            timeout: 180000
        });
    }
    catch (err) {
        log(`build failed: ${err}`);
    }
}
function detectProjectRoot() {
    const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (folder) {
        return folder;
    }
    const document = vscode.window.activeTextEditor?.document;
    if (document?.uri.scheme === 'file') {
        let directory = path.dirname(document.uri.fsPath);
        for (let i = 0; i < 12; i++) {
            if (fs.existsSync(path.join(directory, 'CSharpExtended.sln')) ||
                fs.existsSync(path.join(directory, 'CliTool')) ||
                fs.existsSync(path.join(directory, '.git'))) {
                return directory;
            }
            const parent = path.dirname(directory);
            if (parent === directory) {
                break;
            }
            directory = parent;
        }
        return path.dirname(document.uri.fsPath);
    }
    return process.cwd();
}
function autoDetectLocalCli(root) {
    const candidates = [];
    const repoRoot = path.resolve(extensionPath, '..', '..');
    for (const base of [root, repoRoot]) {
        for (const configuration of ['Debug', 'Release']) {
            candidates.push(path.join(base, 'CliTool', 'Source', 'bin', configuration, 'net10.0', 'CSX.CliTool.exe'));
            candidates.push(path.join(base, 'CliTool', 'Source', 'bin', configuration, 'net10.0', 'CSX.CliTool.dll'));
        }
        candidates.push(path.join(base, 'CSX.CliTool.exe'));
        candidates.push(path.join(base, 'CSX.CliTool.dll'));
    }
    return firstExisting(candidates);
}
function resolveLocalLaunch(setting, root) {
    let target = setting?.trim();
    if (!target) {
        target = autoDetectLocalCli(root) ?? '';
    }
    if (!target) {
        return undefined;
    }
    if (target.endsWith('.csproj')) {
        let built = findBuiltCli(target);
        if (!built) {
            buildProject(target);
            built = findBuiltCli(target);
        }
        if (!built) {
            return undefined;
        }
        target = built;
    }
    if (target.endsWith('.exe')) {
        return { command: target, args: ['lang-server'] };
    }
    if (target.endsWith('.dll')) {
        return { command: 'dotnet', args: [target, 'lang-server'] };
    }
    // A directory: look for a built CLI inside it.
    try {
        if (fs.statSync(target).isDirectory()) {
            const project = firstExisting([
                path.join(target, 'CSX.CliTool.csproj'),
                path.join(target, 'CliTool', 'Source', 'CSX.CliTool.csproj')
            ]);
            if (project) {
                return resolveLocalLaunch(project, root);
            }
            const built = autoDetectLocalCli(target);
            if (built) {
                return resolveLocalLaunch(built, root);
            }
        }
    }
    catch { /* ignore */ }
    return { command: target, args: ['lang-server'] };
}
async function getMode() {
    const configuration = vscode.workspace.getConfiguration('csx');
    const inspected = configuration.inspect('server.mode');
    const explicit = inspected?.workspaceFolderValue ?? inspected?.workspaceValue ?? inspected?.globalValue;
    if (explicit === 'local' || explicit === 'installed') {
        return explicit;
    }
    const choice = await vscode.window.showQuickPick([
        {
            label: '$(folder) Local build',
            description: 'Use the CSX.CliTool built from this repository (recommended for development)',
            value: 'local'
        },
        {
            label: '$(cloud-download) Installed tool',
            description: 'Use the "csx" command from PATH',
            value: 'installed'
        }
    ], {
        placeHolder: 'How should CSX run its language server?',
        ignoreFocusOut: true
    });
    if (!choice) {
        return undefined;
    }
    await configuration.update('server.mode', choice.value, vscode.ConfigurationTarget.Global);
    return choice.value;
}
function setStatus(text, tooltip) {
    if (!status) {
        return;
    }
    status.text = text;
    status.tooltip = tooltip ?? 'CSX language server';
    status.show();
}
async function startServer() {
    if (client || starting) {
        log('startServer: already running or starting');
        return;
    }
    starting = true;
    setStatus('$(sync~spin) CSX: starting', 'Starting the CSX language server...');
    try {
        const mode = await getMode();
        if (!mode) {
            log('startServer: no mode selected');
            setStatus('$(warning) CSX: not configured', 'Run the "CSX: Start Language Server" command to choose a mode.');
            return;
        }
        log(`startServer: mode=${mode}`);
        const root = detectProjectRoot();
        log(`startServer: root=${root}`);
        let command;
        let args;
        if (mode === 'local') {
            const setting = vscode.workspace.getConfiguration('csx').get('local.projectPath', '');
            log(`startServer: local.projectPath=${setting || '(auto)'}`);
            const launch = resolveLocalLaunch(setting, root);
            if (!launch) {
                throw new Error('Could not find a local CSX.CliTool build. Set "csx.local.projectPath" to CSX.CliTool.exe, CSX.CliTool.dll, or CSX.CliTool.csproj.');
            }
            command = launch.command;
            args = launch.args;
        }
        else {
            const configured = vscode.workspace.getConfiguration('csx').get('server.path', 'csx');
            if (!commandExists(configured)) {
                throw new Error(`"${configured}" was not found on PATH. Install it with "dotnet tool install -g CSX.CliTool" or switch "csx.server.mode" to "local".`);
            }
            command = configured;
            args = ['lang-server'];
        }
        args = [...args, '--root', root];
        log(`startServer: launching ${command} ${args.map(a => JSON.stringify(a)).join(' ')}`);
        // Lazy require so a packaging problem with the client library cannot stop activation.
        // eslint-disable-next-line @typescript-eslint/no-var-requires
        const languageclient = require('vscode-languageclient/node');
        const serverOptions = {
            command,
            args,
            transport: languageclient.TransportKind.stdio,
            options: {
                cwd: root
            }
        };
        const clientOptions = {
            documentSelector: [{ scheme: 'file', language: 'cx' }],
            outputChannel: output,
            traceOutputChannel: output,
            synchronize: {
                fileEvents: vscode.workspace.createFileSystemWatcher('**/*.cx')
            },
            initializationOptions: {
                root
            }
        };
        client = new languageclient.LanguageClient('csx', 'CSX Language Server', serverOptions, clientOptions);
        client.onDidChangeState((event) => {
            log(`client state: ${event.oldState} -> ${event.newState}`);
        });
        await client.start();
        setStatus('$(check) CSX: running', 'CSX language server is running.');
        log('startServer: client started');
    }
    catch (err) {
        client = undefined;
        const message = err instanceof Error ? err.message : String(err);
        log(`startServer ERROR: ${err instanceof Error ? err.stack ?? message : message}`);
        setStatus('$(error) CSX: error', message);
        const action = await vscode.window.showErrorMessage(`CSX language server failed: ${message}`, 'Show Log', 'Open Settings');
        if (action === 'Show Log') {
            output?.show(true);
        }
        else if (action === 'Open Settings') {
            await vscode.commands.executeCommand('workbench.action.openSettings', 'csx.');
        }
    }
    finally {
        starting = false;
    }
}
async function restartServer() {
    log('restartServer: requested');
    if (client) {
        try {
            await client.stop();
        }
        catch (err) {
            log(`restartServer: stop failed: ${err}`);
        }
        client = undefined;
    }
    starting = false;
    await startServer();
}
async function activate(context) {
    extensionPath = context.extensionPath;
    output = vscode.window.createOutputChannel('CSX');
    context.subscriptions.push(output);
    status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    status.command = 'csx.showOutput';
    setStatus('$(circle-outline) CSX: idle', 'CSX language server is idle.');
    context.subscriptions.push(status);
    log(`activate: extensionPath=${extensionPath}`);
    log(`activate: vscode=${vscode.version}`);
    context.subscriptions.push(vscode.commands.registerCommand('csx.startServer', () => startServer()), vscode.commands.registerCommand('csx.restartServer', () => restartServer()), vscode.commands.registerCommand('csx.showOutput', () => output?.show(true)));
    const handleOpen = (document) => {
        if (document.languageId === 'cx') {
            log(`cx document opened: ${document.uri.fsPath}`);
            void startServer();
        }
    };
    context.subscriptions.push(vscode.workspace.onDidOpenTextDocument(handleOpen));
    if (vscode.workspace.textDocuments.some(document => document.languageId === 'cx')) {
        log('activate: cx document already open');
        void startServer();
    }
    else {
        log('activate: no cx document open yet');
    }
}
async function deactivate() {
    if (!client) {
        return;
    }
    try {
        await client.stop();
    }
    catch (err) {
        log(`deactivate: stop failed: ${err}`);
    }
    client = undefined;
}
//# sourceMappingURL=extension.js.map