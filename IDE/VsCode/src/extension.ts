import * as vscode from 'vscode';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { execFileSync, spawn } from 'child_process';

let client: any | undefined;
let output: vscode.OutputChannel | undefined;
let status: vscode.StatusBarItem | undefined;
let starting = false;
let extensionPath = '';
let settingsPanel: vscode.WebviewPanel | undefined;

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
        return { command: target, args: [] };
    }
    if (target.endsWith('.dll')) {
        return { command: 'dotnet', args: [target] };
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

    return { command: target, args: [] };
}

function isCliInstalled(): boolean {
    return commandExists('csx');
}

async function installCliTool(): Promise<boolean> {
    log('installing CSX.CliTool from NuGet');
    try {
        await new Promise<void>((resolve, reject) => {
            const proc = spawn('dotnet', ['tool', 'install', '-g', 'CSX.CliTool'], { stdio: 'pipe' });
            let stderr = '';
            proc.stderr?.on('data', (d: Buffer) => { stderr += d.toString(); });
            proc.on('close', (code: number | null) => {
                if (code === 0) resolve();
                else reject(new Error(stderr || `exit code ${code}`));
            });
            proc.on('error', reject);
        });
        log('CSX.CliTool installed successfully');
        return true;
    } catch (err) {
        log(`install failed: ${err}`);
        return false;
    }
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
                label: '$(folder) Local build (dev mode)',
                description: 'Use a CSX.CliTool built from this repository',
                value: 'local' as const
            },
            {
                label: '$(cloud-download) Installed tool',
                description: 'Use the "csx" command from PATH (auto-downloads if missing)',
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

        // Match Rider's `Server args` field: default to `lang-server`, but allow a full override.
        const configuredArgs = vscode.workspace.getConfiguration('csx').get<string[]>('server.args', ['lang-server']);
        const langServerArgs = configuredArgs && configuredArgs.length > 0 ? configuredArgs : ['lang-server'];
        log(`startServer: server.args=${JSON.stringify(langServerArgs)}`);

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
            args = [...launch.args, ...langServerArgs];
        } else {
            // Installed mode: ensure the tool is available, auto-install if missing.
            if (!isCliInstalled()) {
                const install = await vscode.window.showInformationMessage(
                    'CSX.CliTool is not installed. Install it now from NuGet?',
                    'Install', 'Cancel'
                );
                if (install !== 'Install') {
                    throw new Error('CSX.CliTool is not installed. Run "dotnet tool install -g CSX.CliTool" or switch to local mode.');
                }
                const ok = await installCliTool();
                if (!ok || !isCliInstalled()) {
                    throw new Error('Failed to install CSX.CliTool. Check the CSX output channel for details.');
                }
            }
            const configured = vscode.workspace.getConfiguration('csx').get<string>('server.path', 'csx');
            if (!commandExists(configured)) {
                throw new Error(
                    `"${configured}" was not found on PATH. Install it with "dotnet tool install -g CSX.CliTool" or switch "csx.server.mode" to "local".`
                );
            }
            command = configured;
            args = langServerArgs;
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

        const action = await vscode.window.showErrorMessage(`CSX language server failed: ${message}`, 'Show Log', 'Open Settings', 'Install Tool');
        if (action === 'Show Log') {
            output?.show(true);
        } else if (action === 'Open Settings') {
            await vscode.commands.executeCommand('workbench.action.openSettings', 'csx.');
        } else if (action === 'Install Tool') {
            const ok = await installCliTool();
            if (ok) {
                await startServer();
            }
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

function openSettingsPanel() {
    if (settingsPanel) {
        settingsPanel.reveal(vscode.ViewColumn.One);
        return;
    }

    settingsPanel = vscode.window.createWebviewPanel(
        'csxSettings',
        'CSX Settings',
        vscode.ViewColumn.One,
        { enableScripts: true, retainContextWhenHidden: true }
    );

    const config = vscode.workspace.getConfiguration('csx');
    const mode = config.get<string>('server.mode', 'local');
    const localPath = config.get<string>('local.projectPath', '');
    const installed = isCliInstalled();

    settingsPanel.webview.html = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<style>
    body { font-family: var(--vscode-font-family); padding: 20px; color: var(--vscode-foreground); background: var(--vscode-editor-background); }
    h1 { font-size: 1.4em; }
    .section { margin-bottom: 24px; }
    label { display: block; margin-bottom: 6px; font-weight: bold; }
    input[type="text"] { width: 100%; padding: 6px; box-sizing: border-box; background: var(--vscode-input-background); color: var(--vscode-input-foreground); border: 1px solid var(--vscode-input-border); }
    select { padding: 6px; background: var(--vscode-input-background); color: var(--vscode-input-foreground); border: 1px solid var(--vscode-input-border); }
    button { padding: 8px 16px; margin-top: 8px; cursor: pointer; background: var(--vscode-button-background); color: var(--vscode-button-foreground); border: none; }
    button:hover { background: var(--vscode-button-hoverBackground); }
    .status { margin-top: 8px; padding: 8px; border-radius: 4px; }
    .status.ok { background: rgba(0,200,0,0.1); }
    .status.err { background: rgba(200,0,0,0.1); }
    .hidden { display: none; }
</style>
</head>
<body>
    <h1>CSX Settings</h1>

    <div class="section">
        <label for="mode">Server Mode</label>
        <select id="mode">
            <option value="installed" ${mode === 'installed' ? 'selected' : ''}>Installed tool (from NuGet)</option>
            <option value="local" ${mode === 'local' ? 'selected' : ''}>Local build (dev mode)</option>
        </select>
    </div>

    <div class="section" id="installedSection">
        <label>Installed Tool Status</label>
        <div id="installStatus" class="status ${installed ? 'ok' : 'err'}">
            ${installed ? 'CSX.CliTool is installed and available on PATH.' : 'CSX.CliTool is NOT installed.'}
        </div>
        <button id="installBtn">Install / Update from NuGet</button>
        <p style="font-size:0.9em;opacity:0.8;">Installs <code>CSX.CliTool</code> globally via <code>dotnet tool install -g CSX.CliTool</code>.<br>
        Package: <a href="https://www.nuget.org/packages/CSX.CliTool/">nuget.org/packages/CSX.CliTool</a></p>
    </div>

    <div class="section ${mode === 'local' ? '' : 'hidden'}" id="localSection">
        <label for="runtimePath">CSX Runtime Path (dev mode)</label>
        <input type="text" id="runtimePath" value="${localPath}" placeholder="Path to CSX.CliTool.exe, .dll, .csproj, or directory" />
        <p style="font-size:0.9em;opacity:0.8;">Point this at your local CSX.CliTool build output or project file.</p>
    </div>

    <button id="saveBtn">Save &amp; Restart Server</button>

    <script>
        const vscode = acquireVsCodeApi();
        const modeSelect = document.getElementById('mode');
        const installedSection = document.getElementById('installedSection');
        const localSection = document.getElementById('localSection');
        const runtimePath = document.getElementById('runtimePath');
        const installBtn = document.getElementById('installBtn');
        const installStatus = document.getElementById('installStatus');
        const saveBtn = document.getElementById('saveBtn');

        function updateSections() {
            const m = modeSelect.value;
            installedSection.classList.toggle('hidden', m !== 'installed');
            localSection.classList.toggle('hidden', m !== 'local');
        }
        modeSelect.addEventListener('change', updateSections);

        installBtn.addEventListener('click', () => {
            installBtn.disabled = true;
            installBtn.textContent = 'Installing...';
            vscode.postMessage({ command: 'install' });
        });

        saveBtn.addEventListener('click', () => {
            vscode.postMessage({
                command: 'save',
                mode: modeSelect.value,
                runtimePath: runtimePath.value
            });
        });

        window.addEventListener('message', (event) => {
            const msg = event.data;
            if (msg.command === 'installResult') {
                installBtn.disabled = false;
                installBtn.textContent = 'Install / Update from NuGet';
                installStatus.textContent = msg.success ? 'CSX.CliTool is installed and available on PATH.' : 'Installation failed: ' + msg.error;
                installStatus.className = 'status ' + (msg.success ? 'ok' : 'err');
            }
        });
    </script>
</body>
</html>`;

    settingsPanel.onDidDispose(() => { settingsPanel = undefined; });

    settingsPanel.webview.onDidReceiveMessage(async (msg: any) => {
        if (msg.command === 'install') {
            const ok = await installCliTool();
            settingsPanel?.webview.postMessage({
                command: 'installResult',
                success: ok && isCliInstalled(),
                error: ok ? '' : 'See output channel for details.'
            });
        } else if (msg.command === 'save') {
            const cfg = vscode.workspace.getConfiguration('csx');
            await cfg.update('server.mode', msg.mode, vscode.ConfigurationTarget.Global);
            if (msg.mode === 'local') {
                await cfg.update('local.projectPath', msg.runtimePath, vscode.ConfigurationTarget.Global);
            }
            settingsPanel?.dispose();
            await restartServer();
        }
    });
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
        vscode.commands.registerCommand('csx.showOutput', () => output?.show(true)),
        vscode.commands.registerCommand('csx.install', () => installCliTool().then(ok => {
            if (ok) vscode.window.showInformationMessage('CSX.CliTool installed successfully.');
            else vscode.window.showErrorMessage('Failed to install CSX.CliTool. Check the CSX output channel.');
        })),
        vscode.commands.registerCommand('csx.openSettings', () => openSettingsPanel())
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

    // Auto-check: if installed mode and tool missing, prompt.
    const mode = vscode.workspace.getConfiguration('csx').get<string>('server.mode', 'local');
    if (mode === 'installed' && !isCliInstalled()) {
        const action = await vscode.window.showWarningMessage(
            'CSX.CliTool is not installed. Install it now?',
            'Install', 'Open Settings', 'Ignore'
        );
        if (action === 'Install') {
            const ok = await installCliTool();
            if (ok) {
                await startServer();
            }
        } else if (action === 'Open Settings') {
            openSettingsPanel();
        }
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
