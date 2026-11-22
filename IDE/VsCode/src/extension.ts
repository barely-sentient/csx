import * as vscode from 'vscode';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { execFileSync } from 'child_process';

let client: any | undefined;
let output: vscode.OutputChannel | undefined;
let status: vscode.StatusBarItem | undefined;
let starting = false;
let extensionPath = '';

const logFile = path.join(os.tmpdir(), 'csx-extension.log');

function log(message: string) {
    const line = `[${new Date().toISOString()}] ${message}`;
    try { output?.appendLine(line); } catch { /* ignore */ }
    try { fs.appendFileSync(logFile, line + os.EOL); } catch { /* ignore */ }
}

function commandExists(command: string): boolean {
    try {
        if (process.platform === 'win32') {
            execFileSync('where.exe', [command], { stdio: 'ignore' });
        } else {
            execFileSync('which', [command], { stdio: 'ignore' });
        }
        return true;
    } catch {
        return false;
    }
}

function firstExisting(candidates: string[]): string | undefined {
    for (const candidate of candidates) {
        try {
            if (candidate && fs.existsSync(candidate)) {
                return candidate;
            }
        } catch { /* ignore */ }
    }
    return undefined;
}

function findBuiltCli(projectFile: string): string | undefined {
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

function buildProject(projectFile: string) {
    log(`building ${projectFile}`);
    try {
        execFileSync('dotnet', ['build', projectFile, '-c', 'Debug', '-v', 'quiet', '--nologo'], {
            stdio: 'ignore',
            timeout: 180000
        });
    } catch (err) {
        log(`build failed: ${err}`);
    }
}

function detectProjectRoot(): string {
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

function autoDetectLocalCli(root: string): string | undefined {
    const candidates: string[] = [];
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

function resolveLocalLaunch(setting: string, root: string): { command: string; args: string[] } | undefined {
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
    } catch { /* ignore */ }

    return { command: target, args: ['lang-server'] };
}

async function getMode(): Promise<'local' | 'installed' | undefined> {
    const configuration = vscode.workspace.getConfiguration('csx');
    const inspected = configuration.inspect<'local' | 'installed'>('server.mode');
    const explicit = inspected?.workspaceFolderValue ?? inspected?.workspaceValue ?? inspected?.globalValue;
    if (explicit === 'local' || explicit === 'installed') {
        return explicit;
    }

    const choice = await vscode.window.showQuickPick(
        [
            {
                label: '$(folder) Local build',
                description: 'Use the CSX.CliTool built from this repository (recommended for development)',
                value: 'local' as const
            },
            {
                label: '$(cloud-download) Installed tool',
                description: 'Use the "csx" command from PATH',
                value: 'installed' as const
            }
        ],
        {
            placeHolder: 'How should CSX run its language server?',
            ignoreFocusOut: true
        }
    );

    if (!choice) {
        return undefined;
    }

    await configuration.update('server.mode', choice.value, vscode.ConfigurationTarget.Global);
    return choice.value;
}

function setStatus(text: string, tooltip?: string) {
    if (!status) {
        return;
    }
    status.text = text;
    status.tooltip = tooltip ?? 'CSX language server';
    status.show();
}

async function startServer(): Promise<void> {
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

        let command: string;
        let args: string[];

        if (mode === 'local') {
            const setting = vscode.workspace.getConfiguration('csx').get<string>('local.projectPath', '');
            log(`startServer: local.projectPath=${setting || '(auto)'}`);
            const launch = resolveLocalLaunch(setting, root);
            if (!launch) {
                throw new Error(
                    'Could not find a local CSX.CliTool build. Set "csx.local.projectPath" to CSX.CliTool.exe, CSX.CliTool.dll, or CSX.CliTool.csproj.'
                );
            }
            command = launch.command;
            args = launch.args;
        } else {
            const configured = vscode.workspace.getConfiguration('csx').get<string>('server.path', 'csx');
            if (!commandExists(configured)) {
                throw new Error(
                    `"${configured}" was not found on PATH. Install it with "dotnet tool install -g CSX.CliTool" or switch "csx.server.mode" to "local".`
                );
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
        client.onDidChangeState((event: any) => {
            log(`client state: ${event.oldState} -> ${event.newState}`);
        });

        await client.start();
        setStatus('$(check) CSX: running', 'CSX language server is running.');
        log('startServer: client started');
    } catch (err) {
        client = undefined;
        const message = err instanceof Error ? err.message : String(err);
        log(`startServer ERROR: ${err instanceof Error ? err.stack ?? message : message}`);
        setStatus('$(error) CSX: error', message);

        const action = await vscode.window.showErrorMessage(`CSX language server failed: ${message}`, 'Show Log', 'Open Settings');
        if (action === 'Show Log') {
            output?.show(true);
        } else if (action === 'Open Settings') {
            await vscode.commands.executeCommand('workbench.action.openSettings', 'csx.');
        }
    } finally {
        starting = false;
    }
}

async function restartServer(): Promise<void> {
    log('restartServer: requested');
    if (client) {
        try {
            await client.stop();
        } catch (err) {
            log(`restartServer: stop failed: ${err}`);
        }
        client = undefined;
    }
    starting = false;
    await startServer();
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    extensionPath = context.extensionPath;

    output = vscode.window.createOutputChannel('CSX');
    context.subscriptions.push(output);

    status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    status.command = 'csx.showOutput';
    setStatus('$(circle-outline) CSX: idle', 'CSX language server is idle.');
    context.subscriptions.push(status);

    log(`activate: extensionPath=${extensionPath}`);
    log(`activate: vscode=${vscode.version}`);

    context.subscriptions.push(
        vscode.commands.registerCommand('csx.startServer', () => startServer()),
        vscode.commands.registerCommand('csx.restartServer', () => restartServer()),
        vscode.commands.registerCommand('csx.showOutput', () => output?.show(true))
    );

    const handleOpen = (document: vscode.TextDocument) => {
        if (document.languageId === 'cx') {
            log(`cx document opened: ${document.uri.fsPath}`);
            void startServer();
        }
    };

    context.subscriptions.push(vscode.workspace.onDidOpenTextDocument(handleOpen));

    if (vscode.workspace.textDocuments.some(document => document.languageId === 'cx')) {
        log('activate: cx document already open');
        void startServer();
    } else {
        log('activate: no cx document open yet');
    }
}

export async function deactivate(): Promise<void> {
    if (!client) {
        return;
    }
    try {
        await client.stop();
    } catch (err) {
        log(`deactivate: stop failed: ${err}`);
    }
    client = undefined;
}
