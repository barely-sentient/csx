const { execSync } = require('child_process');

function run(cmd) {
    console.log(`> ${cmd}`);
    execSync(cmd, { stdio: 'inherit' });
}

run('npm run compile');
run('npx vsce package --allow-star-activation');